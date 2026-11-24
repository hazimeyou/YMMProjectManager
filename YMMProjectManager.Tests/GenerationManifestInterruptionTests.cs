using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

internal static class GenerationManifestInterruptionTests
{
    internal const string ChildArgument = "--generation-publication-child";
    private const string FixtureMarker = ".synthetic-generation-fixture";
    private const string Snapshot = "synthetic original snapshot";
    private const string ActiveOriginal = "synthetic current PF";
    private const string ExternalFileName = "external-writer.ymmp";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    internal static async Task RunAsync(string workRoot)
    {
        await Program.RunNamedCaseAsync("Manifest lease excludes a second process during generation creation and deletion", async () =>
        {
            var fixture = await Fixture.CreateAsync(workRoot, "manifest-writer-lease");
            using var writer = await StartPausedAsync(fixture, "manifest-before-check", "before-check");
            try
            {
                using var contender = Start(fixture, "second-writer");
                await FinishAsync(contender);
                Require((await fixture.Service.GetGenerationsAsync(fixture.Project)).Count == 1, "Uncommitted data appeared in the manifest.");
                writer.StandardInput.WriteLine("continue");
                await FinishAsync(writer);
                var records = await fixture.Service.GetGenerationsAsync(fixture.Project);
                Require(records.Count == 2 && records.Any(record => record.DisplayName == "first writer") &&
                    records.All(record => record.IsValid), "The single successful update was not reflected.");
            }
            finally { Stop(writer); }

            using var deletion = await StartPausedAsync(fixture, "delete-before-check", "before-check");
            try
            {
                using var contender = Start(fixture, "second-writer");
                await FinishAsync(contender);
                deletion.StandardInput.WriteLine("continue");
                await FinishAsync(deletion);
                var records = await fixture.Service.GetGenerationsAsync(fixture.Project);
                var retired = Directory.EnumerateDirectories(fixture.Storage.GetDeletedDirectory(fixture.ProjectId)).Single();
                Require(records.Count == 1 && records[0].DisplayName == "first writer" && records[0].IsValid &&
                    await File.ReadAllTextAsync(Path.Combine(retired, "project.ymmp")) == Snapshot,
                    "The retirement lost a snapshot or another writer's committed generation.");
            }
            finally { Stop(deletion); }
        });

        await Program.RunNamedCaseAsync("A non-cooperating process claiming the manifest name preserves both versions", async () =>
        {
            var fixture = await Fixture.CreateAsync(workRoot, "manifest-external-claim");
            using var writer = await StartPausedAsync(fixture, "manifest-expect-conflict", "parked");
            try
            {
                using var external = Start(fixture, "external-claim");
                await FinishAsync(external);
                var competing = await ProjectFileEvidence.ReadAsync(fixture.Manifest);
                writer.StandardInput.WriteLine("continue");
                await FinishAsync(writer);

                Require(await ProjectFileEvidence.ReadAsync(fixture.Manifest) == competing &&
                    JsonNode.Parse(await File.ReadAllTextAsync(fixture.Manifest))!["ProjectFileName"]!.GetValue<string>() == ExternalFileName,
                    "The external manifest was replaced.");
                await fixture.VerifyRecoveryVersionsAsync();
            }
            finally { Stop(writer); }
        });

        await Program.RunNamedCaseAsync("A killed manifest writer leaves exact recovery and cannot initialize an empty manifest", async () =>
        {
            var fixture = await Fixture.CreateAsync(workRoot, "manifest-terminated");
            using var writer = await StartPausedAsync(fixture, "manifest-after-parking", "parked");
            Stop(writer);
            Require(writer.ExitCode != 0 && !File.Exists(fixture.Manifest), "The controlled process reached publication.");
            await fixture.VerifyRecoveryVersionsAsync();
            var result = await new ProjectGenerationManifestRepository(fixture.Storage, fixture.Logger)
                .LoadAsync(fixture.ProjectId, fixture.Project);
            Require(result.Manifest is null && result.ErrorMessage?.Contains("manifest.json.previous-", StringComparison.Ordinal) == true,
                "A parked manifest was treated as missing or failed to report its recovery path.");
            await ExpectInvalidAsync(() => fixture.Service.CreateGenerationAsync(fixture.Project, "must not initialize", null));
            Require(!File.Exists(fixture.Manifest), "Recoverable storage was reinitialized.");
        });

        await Program.RunNamedCaseAsync("A killed PF restore records the exact original and refuses unsafe retry", async () =>
        {
            var fixture = await Fixture.CreateAsync(workRoot, "restore-terminated");
            var original = await ProjectFileEvidence.ReadAsync(fixture.Project);
            using var writer = await StartPausedAsync(fixture, "restore-after-parking", "parked");
            Stop(writer);
            Require(writer.ExitCode != 0 && !File.Exists(fixture.Project), "The controlled process reached PF publication.");
            var marker = ProjectGenerationRestoreRecovery.JournalPath(fixture.Project);
            await WaitForFileReleaseAsync(marker);
            using var evidence = JsonDocument.Parse(await File.ReadAllBytesAsync(marker));
            var record = evidence.RootElement;
            var parked = record.GetProperty("RetainedPath").GetString()!;
            var candidate = record.GetProperty("CandidatePath").GetString()!;
            await WaitForFileReleaseAsync(parked);
            await WaitForFileReleaseAsync(candidate);
            Require(record.GetProperty("Version").GetInt32() == 1 &&
                record.GetProperty("TargetPath").GetString() == fixture.Project &&
                Path.GetDirectoryName(parked) == fixture.Storage.GetRestoreBackupsDirectory(fixture.ProjectId) &&
                await ProjectFileEvidence.ReadAsync(parked) == original &&
                await File.ReadAllTextAsync(candidate) == Snapshot,
                "The restart evidence does not identify the original and staged PF.");
            await using (var originalBytes = File.OpenRead(parked))
            {
                var version = await ProjectListReplacement.ReadFileVersionAsync(originalBytes, CancellationToken.None);
                Require(record.GetProperty("Original").GetProperty("FileId").GetGuid() == version.FileId &&
                    record.GetProperty("Original").GetProperty("Sha256").GetString() == version.Sha256,
                    "The original file version was not captured in the recovery record.");
            }

            var retry = await fixture.RestoreAsync();
            Require(!retry.Success && !File.Exists(fixture.Project) && retry.ErrorMessage?.Contains(marker, StringComparison.Ordinal) == true,
                "Retry did not disclose the pending restore or claimed the vacant PF name.");
            await File.WriteAllTextAsync(fixture.Project, "synthetic competing PF");
            var competed = await fixture.RestoreAsync();
            Require(!competed.Success && await File.ReadAllTextAsync(fixture.Project) == "synthetic competing PF" &&
                await ProjectFileEvidence.ReadAsync(parked) == original, "Retry modified a competing PF or its retained original.");
        });
    }

    internal static async Task RunChildAsync(string mode, string root)
    {
        var fixtureBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests")) + Path.DirectorySeparatorChar;
        root = Path.GetFullPath(root);
        if (!root.StartsWith(fixtureBase, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(root, FixtureMarker)))
            throw new InvalidOperationException("Generation child fixture outside the isolated test root.");
        var fixture = new Fixture(root);

        switch (mode)
        {
            case "second-writer":
                await ExpectInvalidAsync(() => fixture.Service.CreateGenerationAsync(fixture.Project, "competing writer", null),
                    "別のプロセス");
                return;
            case "external-claim":
                var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Baseline))!;
                json["ProjectFileName"] = ExternalFileName;
                await using (var output = new FileStream(fixture.Manifest, FileMode.CreateNew))
                    await JsonSerializer.SerializeAsync(output, json);
                return;
            case "restore-after-parking":
                var restore = new ProjectGenerationService(fixture.Logger, fixture.Storage, afterRestoreParking: () => Pause("parked"));
                var result = await restore.RestoreGenerationAsync(fixture.Project, fixture.ExistingGeneration(),
                    GenerationRestoreMode.RestoreToOriginalWithBackup);
                Require(result.Success, result.ErrorMessage ?? "Synthetic restore failed.");
                return;
            case "manifest-before-check":
                var stagedStorage = new ProjectGenerationStorage(fixture.Storage.RootDirectory, null, null, () => Pause("before-check"));
                await new ProjectGenerationService(fixture.Logger, stagedStorage).CreateGenerationAsync(fixture.Project, "first writer", null);
                return;
            case "delete-before-check":
                var retiringStorage = new ProjectGenerationStorage(fixture.Storage.RootDirectory, null, null, () => Pause("before-check"));
                var deletion = await new ProjectGenerationService(fixture.Logger, retiringStorage)
                    .DeleteGenerationAsync(fixture.Project, fixture.ExistingGeneration());
                Require(deletion.Success, deletion.ErrorMessage ?? "Synthetic retirement failed.");
                return;
            case "manifest-after-parking":
            case "manifest-expect-conflict":
                var parkingStorage = new ProjectGenerationStorage(fixture.Storage.RootDirectory, null, null, null, () => Pause("parked"));
                try { await new ProjectGenerationService(fixture.Logger, parkingStorage).CreateGenerationAsync(fixture.Project, "first writer", null); }
                catch (ProjectGenerationManifestPublication.PendingException) when (mode == "manifest-expect-conflict") { return; }
                Require(mode != "manifest-expect-conflict", "Expected a collision after parking the original manifest.");
                return;
            default:
                throw new InvalidOperationException("Unknown synthetic generation child mode.");
        }
    }

    private static void Pause(string phase)
    {
        Console.WriteLine(phase);
        Console.Out.Flush();
        Require(Console.ReadLine() == "continue", "The synthetic publication did not receive its continuation.");
    }

    private static Process Start(Fixture fixture, string mode)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Unable to locate the test process.");
        var start = new ProcessStartInfo(executable) { CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(ChildArgument);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(fixture.Root);
        return Process.Start(start) ?? throw new IOException("Unable to start a synthetic generation test process.");
    }

    private static async Task<Process> StartPausedAsync(Fixture fixture, string mode, string expected)
    {
        var process = Start(fixture, mode);
        try
        {
            var observed = await process.StandardOutput.ReadLineAsync().WaitAsync(Timeout);
            Require(observed == expected, "Synthetic process did not reach " + expected + ": " + observed);
            return process;
        }
        catch
        {
            Stop(process);
            process.Dispose();
            throw;
        }
    }

    private static async Task FinishAsync(Process process)
    {
        try
        {
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(Timeout);
            var detail = await error.WaitAsync(Timeout);
            Require(process.ExitCode == 0, "Synthetic generation child failed: " + detail);
        }
        finally { Stop(process); }
    }

    private static void Stop(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { return; }
        Require(process.WaitForExit((int)Timeout.TotalMilliseconds), "Synthetic generation child did not stop.");
    }

    private static async Task WaitForFileReleaseAsync(string path)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var ready = File.OpenRead(path);
                return;
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020) && watch.Elapsed < Timeout)
            {
                // Process exit can signal before Windows releases its share restrictions.
                await Task.Delay(25);
            }
        }
    }

    private static async Task ExpectInvalidAsync(Func<Task> operation, string? message = null)
    {
        try { await operation(); }
        catch (InvalidDataException ex) when (message is null || ex.Message.Contains(message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A conflicting generation operation did not fail safely.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture(string root)
    {
        internal string Root => root;
        internal string Project => Path.Combine(root, "project.ymmp");
        internal string ProjectId => new ProjectGenerationHashService().ComputeProjectId(Project);
        internal string Manifest => Storage.GetManifestPath(ProjectId);
        internal string Baseline => Path.Combine(root, "manifest.baseline");
        internal ProjectGenerationStorage Storage { get; } = new(Path.Combine(root, "storage"));
        internal FileLogger Logger { get; } = new(Path.Combine(root, "tests.log"));
        internal ProjectGenerationService Service => new(Logger, Storage);

        internal static async Task<Fixture> CreateAsync(string workRoot, string name)
        {
            var root = Path.Combine(workRoot, name);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, FixtureMarker), name);
            var fixture = new Fixture(root);
            await File.WriteAllTextAsync(fixture.Project, Snapshot);
            await fixture.Service.CreateGenerationAsync(fixture.Project, "existing", null);
            File.Copy(fixture.Manifest, fixture.Baseline, overwrite: false);
            await File.WriteAllTextAsync(fixture.Project, ActiveOriginal);
            return fixture;
        }

        internal string ExistingGeneration()
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Baseline));
            return json.RootElement.GetProperty("Generations")[0].GetProperty("GenerationId").GetString()!;
        }

        internal Task<(bool Success, string? ErrorMessage, ProjectGenerationRecord? Generation)> RestoreAsync() =>
            Service.RestoreGenerationAsync(Project, ExistingGeneration(), GenerationRestoreMode.RestoreToOriginalWithBackup);

        internal async Task VerifyRecoveryVersionsAsync()
        {
            var projectDirectory = Storage.GetProjectDirectory(ProjectId);
            var previous = Directory.EnumerateFiles(projectDirectory, "manifest.json.previous-*").Single();
            var staged = Directory.EnumerateFiles(projectDirectory, "manifest.json.tmp-*").Single();
            await WaitForFileReleaseAsync(previous);
            await WaitForFileReleaseAsync(staged);
            var previousBytes = await File.ReadAllBytesAsync(previous);
            var baselineBytes = await File.ReadAllBytesAsync(Baseline);
            Require(previousBytes.AsSpan().SequenceEqual(baselineBytes), "The exact previous manifest bytes were not retained.");
            using var pending = JsonDocument.Parse(await File.ReadAllBytesAsync(staged));
            var generations = pending.RootElement.GetProperty("Generations");
            Require(generations.GetArrayLength() == 2 &&
                await File.ReadAllTextAsync(Storage.GetGenerationFilePath(ProjectId, ExistingGeneration())) == Snapshot &&
                await File.ReadAllTextAsync(Storage.GetGenerationFilePath(ProjectId,
                    generations[1].GetProperty("GenerationId").GetString()!)) == ActiveOriginal,
                "Recovery candidates do not retain both exact generation snapshots.");
        }
    }
}
