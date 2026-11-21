using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

internal static class AssetPoolTests
{
    internal const string ChildArgument = "--r22-asset-pool-collector";
    private const string CommitId = "0123456789abcdef0123456789abcdef01234567";
    private const string ChildReady = "asset-pool-before-publication";
    private static readonly TimeSpan ChildDeadline = TimeSpan.FromSeconds(20);

    internal static async Task RunAsync(string workRoot)
    {
        var root = Directory.CreateDirectory(Path.Combine(workRoot, "asset-pool")).FullName;

        await Program.RunNamedCaseAsync("Asset hashing consumes the selected bytes and leaves stream ownership with the caller", async () =>
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("prefix:payload"));
            content.Position = 7;
            Check(await AssetContentHash.ReadSha256Async(content, CancellationToken.None) == Hash("payload"));
            Check(content.CanRead && content.Position == content.Length);
            content.Position = 0;
            try
            {
                await AssetContentHash.ReadSha256Async(content, new CancellationToken(canceled: true));
                throw new InvalidOperationException("A cancelled hash was accepted.");
            }
            catch (OperationCanceledException)
            {
                Check(content.CanRead);
            }
        });

        await Program.RunNamedCaseAsync("Asset pool keeps the selected source and publishes payload plus immutable manifest", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "pool")).FullName;
            var source = await CreateFileAsync(root, "original", "素材.wav", "owned asset");
            const string reference = @"Media\素材.wav";
            var entry = await AssetPool.CollectAsync(pool, source, reference, CommitId, "https://example.invalid/material", "User reviewed");
            var record = await AssetPool.LoadAsync(entry.ManifestPath);
            Check(record == entry.Record && record is { OriginalReference: reference, CommitId: CommitId,
                OriginalFileName: "素材.wav", RedistributionNote: "User reviewed", DownloadUrl: "https://example.invalid/material" });
            Check(record.Sha256 == Hash("owned asset") && record.Size == 11 &&
                record.DistributionFileName == "素材_" + record.Sha256[..16] + ".wav");
            Check(await File.ReadAllTextAsync(source) == "owned asset" && await File.ReadAllTextAsync(entry.PayloadPath) == "owned asset");
            Check(await AssetPool.CollectAsync(pool, source, reference, CommitId, record.DownloadUrl, record.RedistributionNote) == entry);
            Check(Directory.EnumerateDirectories(pool).SequenceEqual([Path.GetDirectoryName(entry.ManifestPath)!]));
        });

        await Program.RunNamedCaseAsync("Asset pool separates equal names with different bytes and refuses hash-only matches", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "collisions")).FullName;
            var left = await CreateFileAsync(root, "left", "clip.wav", "original content");
            var right = await CreateFileAsync(root, "right", "clip.wav", "other content");
            var renamed = await CreateFileAsync(root, "renamed", "alias.wav", "original content");
            var first = await AssetPool.CollectAsync(pool, left, "clip.wav", CommitId);
            var second = await AssetPool.CollectAsync(pool, right, "clip.wav", CommitId);
            var alias = await AssetPool.CollectAsync(pool, renamed, "alias.wav", CommitId);
            Check(first.PayloadPath != second.PayloadPath && alias.PayloadPath != first.PayloadPath && alias.Record.Sha256 == first.Record.Sha256);
            Check((await AssetPool.FindAsync("clip.wav", first.Record.Sha256, [pool])).Single().CandidatePath == first.PayloadPath);
            Check((await AssetPool.FindAsync("clip.wav", second.Record.Sha256, [pool])).Single().CandidatePath == second.PayloadPath);
            Check((await AssetPool.FindAsync("clip.wav", first.Record.Sha256, [pool], alias.ManifestPath)).Count == 0);
            Check((await AssetPool.FindAsync("clip.wav", null, [pool])).Count == 0);
        });

        await Program.RunNamedCaseAsync("Competing collection of the same asset publishes one complete entry", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "parallel")).FullName;
            var source = await CreateFileAsync(root, "parallel-source", "clip.wav", "parallel asset");
            using var barrier = new Barrier(2);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<AssetPoolEntry> Collect() => Task.Run(() => AssetPool.CollectCoreAsync(pool, source, "clip.wav", CommitId, null, null,
                () => barrier.SignalAndWait(deadline.Token), deadline.Token), deadline.Token);
            var entries = await Task.WhenAll(Collect(), Collect());
            Check(entries[0] == entries[1] && Directory.EnumerateDirectories(pool).Count() == 1);
            Check(await File.ReadAllTextAsync(entries[0].PayloadPath) == "parallel asset");
        });

        await Program.RunNamedCaseAsync("Independent asset collectors publish the exact same complete entry", async () =>
        {
            var (fixture, pool, source) = await PrepareChildAsync(root, "process-race", "multi-process");
            using var first = StartCollector(fixture);
            using var second = StartCollector(fixture);
            try
            {
                await Task.WhenAll(WaitForPublicationAsync(first), WaitForPublicationAsync(second));
                Check(Directory.GetDirectories(pool, ".asset-pool-pending-*").Length == 2);
                Check(Directory.GetDirectories(pool, "asset-*").Length == 0);
                await Task.WhenAll(PublishAsync(first), PublishAsync(second));
                var entries = Directory.GetDirectories(pool);
                Check(entries.Length == 1);
                var manifest = Path.Combine(entries[0], AssetPool.ManifestFileName);
                var record = await AssetPool.LoadAsync(manifest);
                Check(record.Sha256 == Hash("multi-process"));
                Check(await File.ReadAllTextAsync(Path.Combine(entries[0], record.DistributionFileName)) == "multi-process");
                Check(await File.ReadAllTextAsync(source) == "multi-process");
            }
            finally
            {
                await Task.WhenAll(StopIfRunningAsync(first), StopIfRunningAsync(second));
            }
        });

        await Program.RunNamedCaseAsync("Terminated collector leaves unowned pending data and retry publishes safely", async () =>
        {
            var (fixture, pool, source) = await PrepareChildAsync(root, "process-crash", "interrupted process");
            using var child = StartCollector(fixture);
            try
            {
                await WaitForPublicationAsync(child);
                var pending = Directory.GetDirectories(pool, ".asset-pool-pending-*").Single();
                var pendingFiles = Directory.GetFiles(pending);
                Check(pendingFiles.Length == 2 && Directory.GetDirectories(pool, "asset-*").Length == 0);
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(ChildDeadline);
                Check(child.ExitCode != 0);
                var pendingSnapshot = new Dictionary<string, byte[]>();
                foreach (var file in pendingFiles) pendingSnapshot.Add(file, await ReadAfterReleaseAsync(file));
                Check((await AssetPool.FindAsync("clip.wav", Hash("interrupted process"), [pool])).Count == 0);
                var entry = await AssetPool.CollectAsync(pool, source, "clip.wav", CommitId);
                Check((await AssetPool.FindAsync("clip.wav", entry.Record.Sha256, [pool])).Single().CandidatePath == entry.PayloadPath);
                Check(Directory.GetDirectories(pool, "asset-*").Length == 1);
                Check(pendingSnapshot.All(item => File.ReadAllBytes(item.Key).AsSpan().SequenceEqual(item.Value)));
                Check(await File.ReadAllTextAsync(source) == "interrupted process");
            }
            finally
            {
                await StopIfRunningAsync(child);
            }
        });

        await Program.RunNamedCaseAsync("Asset pool preserves third-party staging entries and refuses to publish them", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "unexpected-stage")).FullName;
            var source = await CreateFileAsync(root, "unexpected-source", "clip.wav", "unchanged");
            string? unknown = null;
            await ThrowsAsync<IOException>(() => AssetPool.CollectCoreAsync(pool, source, "clip.wav", CommitId, null, null, () =>
            {
                var stage = Directory.GetDirectories(pool, ".asset-pool-pending-*").Single();
                unknown = Path.Combine(stage, "other-owner.txt");
                File.WriteAllText(unknown, "belongs to another process");
            }, default));
            Check(unknown is not null && File.ReadAllText(unknown) == "belongs to another process");
            Check(Directory.GetFiles(Path.GetDirectoryName(unknown!)!).SequenceEqual([unknown!]));
            Check(Directory.GetDirectories(pool, "asset-*").Length == 0);
            var entry = await AssetPool.CollectAsync(pool, source, "clip.wav", CommitId);
            var extra = Path.Combine(Path.GetDirectoryName(entry.ManifestPath)!, "recording.txt");
            await File.WriteAllTextAsync(extra, "preserve this extra entry");
            await ThrowsAsync<IOException>(() => AssetPool.CollectAsync(pool, source, "clip.wav", CommitId));
            Check(await File.ReadAllTextAsync(extra) == "preserve this extra entry");
            Check(await File.ReadAllTextAsync(source) == "unchanged");
        });

        await Program.RunNamedCaseAsync("Asset pool refuses overwriting an existing modified entry", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "modified-entry")).FullName;
            var source = await CreateFileAsync(root, "modified-source", "clip.wav", "my asset");
            var entry = await AssetPool.CollectAsync(pool, source, "clip.wav", CommitId);
            await File.WriteAllTextAsync(entry.PayloadPath, "someone else's copy");
            await ThrowsAsync<IOException>(() => AssetPool.CollectAsync(pool, source, "clip.wav", CommitId));
            Check(await File.ReadAllTextAsync(entry.PayloadPath) == "someone else's copy" && await File.ReadAllTextAsync(source) == "my asset");
        });

        await Program.RunNamedCaseAsync("Strict relink returns every exact match and holds only the selected approved file", async () =>
        {
            var first = await CreateFileAsync(root, "trusted-first", "clip.wav", "exact");
            var second = await CreateFileAsync(root, "trusted-second", "clip.wav", "exact");
            var other = await CreateFileAsync(root, "unapproved", "clip.wav", "exact");
            var roots = new[] { Path.GetDirectoryName(first)!, Path.GetDirectoryName(second)! };
            var matches = await AssetPool.FindAsync("clip.wav", Hash("exact"), roots);
            Check(matches.Count == 2 && matches.All(entry => entry.CandidatePath != other));
            var reviewed = matches.Single(item => item.CandidatePath == second);
            await using (var held = await AssetPool.OpenVerifiedAsync(reviewed, "clip.wav", Hash("exact")))
            {
                Check(held.CandidatePath == second);
                await ThrowsAsync<IOException>(() => File.WriteAllTextAsync(second, "blocked"));
            }
            await File.WriteAllTextAsync(second, "changed bytes");
            await ThrowsAsync<InvalidDataException>(async () =>
            {
                await using var held = await AssetPool.OpenVerifiedAsync(reviewed, "clip.wav", Hash("exact"));
            });
        });

        await Program.RunNamedCaseAsync("Strict relink validates manifest location, fields, and changes without fetching a URL", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "verified-map")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "other-map")).FullName;
            var source = await CreateFileAsync(root, "map-source", "clip.wav", "exact");
            var entry = await AssetPool.CollectAsync(pool, source, "clip.wav", CommitId, "https://example.invalid/asset");
            var match = (await AssetPool.FindAsync("clip.wav", Hash("exact"), [pool], entry.ManifestPath)).Single();
            await ThrowsAsync<InvalidDataException>(() => AssetPool.FindAsync("clip.wav", Hash("exact"), [outside], entry.ManifestPath));
            var altered = entry.Record with { RedistributionNote = "unreviewed" };
            await File.WriteAllTextAsync(entry.ManifestPath, JsonSerializer.Serialize(altered));
            await ThrowsAsync<InvalidDataException>(async () =>
            {
                await using var held = await AssetPool.OpenVerifiedAsync(match, "clip.wav", Hash("exact"));
            });
            var escaped = altered with { DistributionFileName = @"..\clip.wav" };
            await File.WriteAllTextAsync(entry.ManifestPath, JsonSerializer.Serialize(escaped));
            await ThrowsAsync<InvalidDataException>(() => AssetPool.FindAsync("clip.wav", Hash("exact"), [pool], entry.ManifestPath));
            var duplicate = JsonSerializer.Serialize(altered).Replace("{", "{\"Size\":0,", StringComparison.Ordinal);
            await File.WriteAllTextAsync(entry.ManifestPath, duplicate);
            await ThrowsAsync<JsonException>(() => AssetPool.LoadAsync(entry.ManifestPath));
        });

        await Program.RunNamedCaseAsync("Asset pool requires the recorded size even for an empty payload", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "empty-pool")).FullName;
            var source = await CreateFileAsync(root, "empty-source", "empty.wav", "");
            var entry = await AssetPool.CollectAsync(pool, source, "empty.wav", CommitId);
            Check((await AssetPool.LoadAsync(entry.ManifestPath)).Size == 0);
            Check((await AssetPool.FindAsync("empty.wav", Hash(""), [pool], entry.ManifestPath)).Count == 1);
            var omitted = JsonSerializer.SerializeToNode(entry.Record)!.AsObject();
            Check(omitted.Remove(nameof(AssetPoolRecord.Size)));
            await File.WriteAllTextAsync(entry.ManifestPath, omitted.ToJsonString());
            await ThrowsAsync<JsonException>(() => AssetPool.LoadAsync(entry.ManifestPath));
            await ThrowsAsync<JsonException>(() => AssetPool.FindAsync("empty.wav", Hash(""), [pool], entry.ManifestPath));
        });

        await Program.RunNamedCaseAsync("Asset pool switches storage roots and preserves an unreadable existing entry", async () =>
        {
            var first = Directory.CreateDirectory(Path.Combine(root, "storage-first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "storage-second")).FullName;
            var source = await CreateFileAsync(root, "storage-source", "clip.wav", "preserve bytes");
            var oldEntry = await AssetPool.CollectAsync(first, source, "clip.wav", CommitId);
            var newEntry = await AssetPool.CollectAsync(second, source, "clip.wav", CommitId);
            var manifestBefore = await File.ReadAllBytesAsync(newEntry.ManifestPath);
            var payloadBefore = await File.ReadAllBytesAsync(newEntry.PayloadPath);
            Check(newEntry.Record == oldEntry.Record && newEntry.PayloadPath != oldEntry.PayloadPath);
            using (File.Open(newEntry.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await ThrowsAsync<IOException>(() => AssetPool.LoadAsync(newEntry.ManifestPath));
                await ThrowsAsync<IOException>(() => AssetPool.FindAsync("clip.wav", newEntry.Record.Sha256, [second]));
                await ThrowsAsync<IOException>(() => AssetPool.CollectAsync(second, source, "clip.wav", CommitId));
            }
            using (File.Open(newEntry.PayloadPath, FileMode.Open, FileAccess.Read, FileShare.None))
                await ThrowsAsync<IOException>(() => AssetPool.FindAsync("clip.wav", newEntry.Record.Sha256, [second]));
            Check(File.ReadAllBytes(newEntry.ManifestPath).AsSpan().SequenceEqual(manifestBefore));
            Check(File.ReadAllBytes(newEntry.PayloadPath).AsSpan().SequenceEqual(payloadBefore));
            Check(File.ReadAllBytes(oldEntry.PayloadPath).AsSpan().SequenceEqual(payloadBefore));
            Check(await AssetPool.CollectAsync(second, source, "clip.wav", CommitId) == newEntry);
        });

        await Program.RunNamedCaseAsync("Ordinary UNC syntax is checked lexically before manifest scope and any I/O", async () =>
        {
            var localManifest = Path.Combine(root, AssetPool.ManifestFileName);
            await ThrowsAsync<InvalidDataException>(() => AssetPool.FindAsync("clip.wav", Hash("expected"),
                [@"\\asset-pool-fixture.invalid\synthetic-share\assets"], localManifest));
            await ThrowsAsync<ArgumentException>(() => AssetPool.FindAsync("clip.wav", Hash("expected"),
                [@"\\asset-pool-fixture.invalid"], localManifest));
        });

        await Program.RunNamedCaseAsync("Rehydrated asset proof rejects noncanonical and disjoint manifest paths", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "rehydrated-proof")).FullName;
            var source = await CreateFileAsync(root, "rehydrated-source", "clip.wav", "exact");
            var entry = await AssetPool.CollectAsync(pool, source, "clip.wav", CommitId);
            var match = (await AssetPool.FindAsync("clip.wav", Hash("exact"), [pool], entry.ManifestPath)).Single();
            var leaf = Path.GetFileName(match.CandidatePath);
            var directory = Path.GetDirectoryName(match.CandidatePath)!;
            foreach (var candidate in new[]
            {
                new AssetPoolMatch(Path.Combine(directory, ".", leaf), match.ManifestPath, match.ManifestSha256),
                new AssetPoolMatch(match.CandidatePath, Path.Combine(root, AssetPool.ManifestFileName), match.ManifestSha256),
                new AssetPoolMatch(match.CandidatePath, null, match.ManifestSha256),
            })
            {
                await ThrowsAsync<InvalidDataException>(async () =>
                {
                    await using var held = await AssetPool.OpenVerifiedAsync(candidate, "clip.wav", Hash("exact"));
                });
            }
            await using var approved = await AssetPool.OpenVerifiedAsync(match, "clip.wav", Hash("exact"));
            Check(approved.CandidatePath == entry.PayloadPath);
        });

        await Program.RunNamedCaseAsync("Asset pool rejects device namespace spellings and case-ambiguous manifest scope", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "scope-lower")).FullName;
            var source = await CreateFileAsync(root, "scope-source", "clip.wav", "scope check");
            var entry = await AssetPool.CollectAsync(pool, source, "clip.wav", CommitId);
            // Every spelling points only at the synthetic fixture, even if a namespace were accepted.
            var ordinarySlashPath = entry.ManifestPath.Replace('\\', '/');
            foreach (var prefix in new[] { "//?/", "//./", @"\\?\", @"\\.\" })
                await ThrowsAsync<ArgumentException>(() => AssetPool.LoadAsync(prefix + ordinarySlashPath));

            var caseDifferentApproval = Path.Combine(root, "SCOPE-LOWER");
            await ThrowsAsync<InvalidDataException>(() =>
                AssetPool.FindAsync("clip.wav", entry.Record.Sha256, [caseDifferentApproval], entry.ManifestPath));
            Check((await AssetPool.FindAsync("clip.wav", entry.Record.Sha256, [pool], entry.ManifestPath)).Count == 1);
        });

        await Program.RunNamedCaseAsync("Strict relink refuses an unapproved directory alias", async () =>
        {
            var outside = await CreateFileAsync(root, "external", "outside.wav", "same");
            var approved = Directory.CreateDirectory(Path.Combine(root, "approved")).FullName;
            var alias = Path.Combine(approved, "alias");
            var junction = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", alias, Path.GetDirectoryName(outside)! }) junction.ArgumentList.Add(argument);
            using var process = Process.Start(junction)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            await stderr;
            if (process.ExitCode != 0) { Program.ReportConditionalSkip("junction fixture unavailable"); return; }
            await ThrowsAsync<IOException>(() => AssetPool.FindAsync("outside.wav", Hash("same"), [alias]));
            Check((await AssetPool.FindAsync("outside.wav", Hash("same"), [approved])).Count == 0);
            // Remove only this verified junction so generic fixture cleanup never follows it.
            Directory.Delete(alias);
        });

        await Program.RunNamedCaseAsync("Asset pool interruption and simulated share disconnection leave a safe retry", async () =>
        {
            var pool = Directory.CreateDirectory(Path.Combine(root, "interrupted")).FullName;
            var source = await CreateFileAsync(root, "interrupted-source", "clip.wav", "survives");
            using var cancellation = new CancellationTokenSource();
            await ThrowsAsync<OperationCanceledException>(() => AssetPool.CollectCoreAsync(pool, source, "clip.wav", CommitId,
                null, null, cancellation.Cancel, cancellation.Token));
            Check(!Directory.EnumerateFileSystemEntries(pool).Any());
            await ThrowsAsync<IOException>(() => AssetPool.CollectCoreAsync(pool, source, "clip.wav", CommitId,
                null, null, () => throw new IOException("Simulated share disconnect before publication"), default));
            Check(!Directory.EnumerateFileSystemEntries(pool).Any() && await File.ReadAllTextAsync(source) == "survives");
            var entry = await AssetPool.CollectAsync(pool, source, "clip.wav", CommitId);
            Check((await AssetPool.FindAsync("clip.wav", entry.Record.Sha256, [pool])).Single().CandidatePath == entry.PayloadPath);
        });

        await Program.RunNamedCaseAsync("Strict received-PF scan reads references without probing the PF's reference paths", async () =>
        {
            var supplied = await CreateFileAsync(root, "pf-local", "local.wav", "file actually exists");
            const string forbidden = @"\\unapproved.invalid\payloads\private.wav";
            var pf = await CreateFileAsync(root, "received", "project.ymmp",
                JsonSerializer.Serialize(new { Items = new[] { new { FilePath = supplied }, new { FilePath = forbidden } } }));
            var scan = await new RelinkScanService(new FileLogger(Path.Combine(root, "strict-scan.log"))).ScanForStrictRelinkAsync(pf, default);
            Check(scan.Result.MissingCount == 2 && scan.Context?.Rows.All(row => row.Message == "元の参照先は未確認") == true);
            Check(AssetPool.GetOriginalFileName(forbidden) == "private.wav");
        });

        await Program.RunNamedCaseAsync("Approved strict relink can Save As without changing the received project", async () =>
        {
            const string inputReference = @"\\unapproved.invalid\media\clip.wav";
            var matching = await CreateFileAsync(root, "chosen-root", "clip.wav", "chosen data");
            var projectSource = JsonSerializer.Serialize(new { Items = new[] { new { FilePath = inputReference, UnknownFlag = true } } });
            var project = await CreateFileAsync(root, "saveas", "project.ymmp", projectSource);
            var logger = new FileLogger(Path.Combine(root, "saveas.log"));
            var (context, _) = await new RelinkScanService(logger).ScanForStrictRelinkAsync(project, default);
            var candidate = (await AssetPool.FindAsync("clip.wav", Hash("chosen data"), [Path.GetDirectoryName(matching)!])).Single();
            await using var held = await AssetPool.OpenVerifiedAsync(candidate, "clip.wav", Hash("chosen data"));
            var row = context!.Rows.Single();
            row.SelectedCandidate = held.CandidatePath;
            row.Status = RelinkStatus.Updated;
            var output = Path.Combine(Path.GetDirectoryName(project)!, "output.ymmp");
            var saved = await new RelinkSaveService(logger).SaveAsAsync(context, output, default);
            Check(saved.Success && (await File.ReadAllTextAsync(project)) == projectSource);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var result = document.RootElement.GetProperty("Items")[0];
            Check(result.GetProperty("FilePath").GetString() == matching && result.GetProperty("UnknownFlag").GetBoolean());
        });
    }

    private static async Task<string> CreateFileAsync(string root, string folder, string name, string content)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, folder)).FullName, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    internal static async Task RunChildAsync(string fixture)
    {
        var testBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests"));
        var full = Path.GetFullPath(fixture);
        var relative = Path.GetRelativePath(testBase, full).Split(Path.DirectorySeparatorChar);
        if (fixture != full || relative.Length != 3 || !Guid.TryParseExact(relative[0], "N", out _) ||
            relative[1] != "asset-pool" || relative[2] is not ("process-race" or "process-crash") ||
            !full.StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Asset collector fixture is outside the isolated test directory.");
        using var directories = OwnedFilePublication.DirectoryFence.Open(full, enforceShare: true);
        var source = Path.Combine(full, "original", "clip.wav");
        var pool = Path.Combine(full, "pool");
        await AssetPool.CollectCoreAsync(pool, source, "clip.wav", CommitId, null, null, () =>
        {
            Console.WriteLine(ChildReady);
            if (Console.ReadLine() != "publish") throw new InvalidOperationException("Asset collector did not receive publication consent.");
        }, default);
    }

    private static async Task<(string Fixture, string Pool, string Source)> PrepareChildAsync(string root, string name, string text)
    {
        var fixture = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var pool = Directory.CreateDirectory(Path.Combine(fixture, "pool")).FullName;
        var source = await CreateFileAsync(fixture, "original", "clip.wav", text);
        return (fixture, pool, source);
    }

    private static Process StartCollector(string fixture)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(AssetPoolTests).Assembly.Location);
        start.ArgumentList.Add(ChildArgument);
        start.ArgumentList.Add(fixture);
        return Process.Start(start) ?? throw new InvalidOperationException("Asset collector fixture could not start.");
    }

    private static async Task WaitForPublicationAsync(Process child)
    {
        var readiness = await child.StandardOutput.ReadLineAsync().WaitAsync(ChildDeadline);
        if (readiness != ChildReady)
            throw new InvalidOperationException($"Asset collector missed the publication barrier: {readiness}");
    }

    private static async Task PublishAsync(Process child)
    {
        await child.StandardInput.WriteLineAsync("publish");
        child.StandardInput.Close();
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        await Task.WhenAll(child.WaitForExitAsync(), stdout, stderr).WaitAsync(ChildDeadline);
        if (child.ExitCode != 0)
            throw new InvalidOperationException($"Asset collector exited with {child.ExitCode}: {await stderr}");
    }

    private static async Task StopIfRunningAsync(Process child)
    {
        try { if (!child.HasExited) child.Kill(); }
        catch (InvalidOperationException) when (child.HasExited) { }
        await child.WaitForExitAsync().WaitAsync(ChildDeadline);
    }

    private static async Task<byte[]> ReadAfterReleaseAsync(string path)
    {
        using var deadline = new CancellationTokenSource(ChildDeadline);
        while (true)
        {
            // On Windows a terminated process can signal before all its owned
            // handles have detached. Observe release before taking the snapshot.
            try { return await File.ReadAllBytesAsync(path, deadline.Token); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
        }
    }

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void Check(bool result, [CallerArgumentExpression(nameof(result))] string? expression = null)
    {
        if (!result) throw new InvalidOperationException($"Asset pool safety check failed: {expression}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{typeof(T).Name} was expected.");
    }
}
