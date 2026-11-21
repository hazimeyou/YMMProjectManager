using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

internal static class AssetPoolTests
{
    private const string CommitId = "0123456789abcdef0123456789abcdef01234567";

    internal static async Task RunAsync(string workRoot)
    {
        var root = Directory.CreateDirectory(Path.Combine(workRoot, "asset-pool")).FullName;

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
