using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Settings;

internal static class SettingsAndDiagnosticsTests
{
    internal static async Task RunAsync(string workRoot)
    {
        var root = Directory.CreateDirectory(Path.Combine(workRoot, "r28-settings")).FullName;
        var older = Path.Combine(root, "folder-a");
        var newer = Path.Combine(root, "folder-b");
        await Program.RunNamedCaseAsync("Logger startup tolerates an unavailable log directory", () =>
        {
            var blocked = Path.Combine(root, "blocked-log");
            File.WriteAllText(blocked, "unrelated file");
            var logger = new FileLogger(Path.Combine(blocked, "test.log"));
            logger.Info("best effort");
            logger.Flush();
            logger.Close();
            Require(File.ReadAllText(blocked) == "unrelated file", "Logging changed the blocked path.");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Global exception summary excludes raw messages and exception data", () =>
        {
            var fault = new InvalidOperationException("synthetic private message");
            fault.Data["private"] = "synthetic private data";
            var summary = PluginDiagnostics.DescribeException(fault);
            Require(summary.Contains(nameof(InvalidOperationException), StringComparison.Ordinal) &&
                !summary.Contains("synthetic private", StringComparison.Ordinal), "Incorrect exception summary.");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Settings failure restores the disk and committed folders", () =>
        {
            var path = Path.Combine(root, "rollback.json");
            var original = new YMMProjectManagerSettings(path);
            Require(original.SetSearchFolders([older]), "Initial save failed.");
            var bytes = File.ReadAllBytes(path);
            var legacyTemp = path + ".tmp";
            File.WriteAllText(legacyTemp, "owned by someone else");
            var subject = new YMMProjectManagerSettings(path, () => throw new IOException("fixture fault"));
            Require(!subject.SetSearchFolders([newer]), "Failure was accepted.");
            Require(subject.GetSearchFolders().SequenceEqual([older]) && File.ReadAllBytes(path).SequenceEqual(bytes),
                "The committed settings changed.");
            Require(File.ReadAllText(legacyTemp) == "owned by someone else" &&
                Directory.EnumerateFiles(root).Where(file => file.StartsWith(path + ".", StringComparison.Ordinal)).SequenceEqual([legacyTemp]), "Cleanup affected an unrelated file.");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Settings save retains a competing file and the original revision", () =>
        {
            var path = Path.Combine(root, "occupied.json");
            var original = new YMMProjectManagerSettings(path);
            Require(original.SetSearchFolders([older]), "Initial save failed.");
            var bytes = File.ReadAllBytes(path);
            var subject = new YMMProjectManagerSettings(path, () =>
            {
                using var intruder = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                intruder.Write("competing version"u8);
            });
            Require(!subject.SetSearchFolders([newer]), "Conflict was accepted.");
            Require(File.ReadAllText(path) == "competing version" && subject.GetSearchFolders().SequenceEqual([older]),
                "Concurrent or committed data changed.");
            var backup = Directory.GetFiles(root, "occupied.json.previous-*").Single();
            Require(subject.RetainedBackupPath == backup && bytes.SequenceEqual(File.ReadAllBytes(backup)),
                "Original settings were not preserved or the recovery path was not reported.");
            Require(!subject.TryReload() && subject.GetSearchFolders().SequenceEqual([older]), "Corrupt input discarded the confirmed settings.");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Existing settings with a Unicode BOM keep the established JSON format", () =>
        {
            var path = Path.Combine(root, "unicode.json");
            File.WriteAllText(path, """{"SearchFolders":["素材"]}""", Encoding.Unicode);
            var settings = new YMMProjectManagerSettings(path);
            Require(settings.GetSearchFolders().Count == 1 &&
                Path.GetFileName(settings.GetSearchFolders()[0]) == "素材", "Legacy Unicode settings unreadable.");
            Require(settings.SetSearchFolders([newer]), "Save failed.");
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            Require(json.RootElement.GetProperty("SearchFolders")[0].GetString() == newer, "Settings schema changed.");
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Settings refuse to replace an original with unsupported file attributes", () =>
        {
            var path = Path.Combine(root, "read-only.json");
            var settings = new YMMProjectManagerSettings(path);
            Require(settings.SetSearchFolders([older]), "Initial save failed.");
            var original = File.ReadAllBytes(path);
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            try
            {
                Require(!settings.SetSearchFolders([newer]) && settings.GetSearchFolders().SequenceEqual([older]) &&
                    original.SequenceEqual(File.ReadAllBytes(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly),
                    "Read-only settings lost contents, metadata or in-memory state.");
            }
            finally { File.SetAttributes(path, attributes); }
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("Settings reject stale concurrent saves and support reloading before retry", async () =>
        {
            var path = Path.Combine(root, "concurrent.json");
            var first = new YMMProjectManagerSettings(path);
            Require(first.SetSearchFolders([older]), "Initial save failed.");
            var second = new YMMProjectManagerSettings(path);
            var attempted = await Task.WhenAll(Task.Run(() => first.SetSearchFolders([newer])),
                Task.Run(() => second.SetSearchFolders([root])));
            Require(attempted.Count(success => success) == 1, "Two stale settings publications succeeded.");
            var loser = attempted[0] ? second : first;
            var winner = attempted[0] ? newer : root;
            Require(loser.GetSearchFolders().SequenceEqual([older]) &&
                new YMMProjectManagerSettings(path).GetSearchFolders().SequenceEqual([winner]), "Committed states disagree.");
            Require(loser.TryReload() && loser.SetSearchFolders([older]) &&
                new YMMProjectManagerSettings(path).GetSearchFolders().SequenceEqual([older]), "Reload and retry failed.");
        });
        await Program.RunNamedCaseAsync("Settings recover the exact original after an OS process termination", async () =>
        {
            var interrupted = await InterruptSaveAsync(root, older);
            var retained = new YMMProjectManagerSettings(interrupted.Path);
            Require(!retained.TryReload() && !retained.SetSearchFolders([newer]), "A missing current file bypassed recovery.");
            var candidate = retained.FindInterruptedSave();
            Require(candidate is not null && candidate.BackupPath == interrupted.Backup, "No verified recovery was found.");
            using (var backup = File.OpenRead(interrupted.Backup))
                Require(ProjectFileEvidence.ReadPhysicalIdentity(backup) == interrupted.Identity, "The parked original changed identity.");

            Require(retained.TryRecoverInterruptedSave(candidate!), "Explicit recovery failed.");
            using (var restored = File.OpenRead(interrupted.Path))
                Require(ProjectFileEvidence.ReadPhysicalIdentity(restored) == interrupted.Identity, "Recovery replaced the original file identity.");
            Require(File.ReadAllBytes(interrupted.Path).SequenceEqual(interrupted.Before) &&
                retained.GetSearchFolders().SequenceEqual([older]) &&
                !File.Exists(interrupted.Receipt) && !retained.TryRecoverInterruptedSave(candidate!),
                "Recovery changed the old bytes or was incorrectly repeatable.");
            Require(retained.SetSearchFolders([newer]), "Normal save did not resume after recovery.");
        });
        await Program.RunNamedCaseAsync("Settings recovery refuses a new current file and keeps the proven backup", async () =>
        {
            var interrupted = await InterruptSaveAsync(root, older);
            const string competitor = "{\"SearchFolders\":[]}";
            var retained = new YMMProjectManagerSettings(interrupted.Path, () =>
            {
                using var stream = new FileStream(interrupted.Path, FileMode.CreateNew, FileAccess.Write);
                stream.Write(Encoding.UTF8.GetBytes(competitor));
            });
            var candidate = retained.FindInterruptedSave();
            Require(candidate is not null, "The parked original was not detected.");
            Require(!retained.TryRecoverInterruptedSave(candidate!) && File.ReadAllText(interrupted.Path) == competitor &&
                File.ReadAllBytes(interrupted.Backup).SequenceEqual(interrupted.Before) && File.Exists(interrupted.Receipt),
                "Recovery overwrote competing settings or cleaned up evidence.");
        });
        await Program.RunNamedCaseAsync("Settings recovery refuses changed bytes, multiple backups and invalid evidence", async () =>
        {
            var interrupted = await InterruptSaveAsync(root, older);
            var retained = new YMMProjectManagerSettings(interrupted.Path);
            var candidate = retained.FindInterruptedSave();
            Require(candidate is not null, "The parked original was not detected.");
            var second = interrupted.Path + ".previous-" + Guid.NewGuid().ToString("N");
            File.Copy(interrupted.Backup, second);
            Require(retained.FindInterruptedSave() is null && !retained.TryRecoverInterruptedSave(candidate!),
                "Ambiguous backups allowed recovery.");
            File.Delete(second);

            File.WriteAllText(interrupted.Backup, "{\"SearchFolders\":[]}");
            Require(retained.FindInterruptedSave() is null && !retained.TryRecoverInterruptedSave(candidate!),
                "Changed backup contents were accepted.");
            File.WriteAllBytes(interrupted.Backup, interrupted.Before);
            Require(retained.FindInterruptedSave() == candidate, "Restoring fixture bytes should restore the evidence.");
            File.WriteAllText(interrupted.Receipt, "{corrupt");
            Require(retained.FindInterruptedSave() is null && !retained.TryRecoverInterruptedSave(candidate!) &&
                !File.Exists(interrupted.Path) && File.ReadAllBytes(interrupted.Backup).SequenceEqual(interrupted.Before) &&
                File.ReadAllText(interrupted.Receipt) == "{corrupt", "Corrupt evidence was accepted or removed.");
        });
        await Program.RunNamedCaseAsync("Settings recovery refuses to move another directory's retained settings into place", async () =>
        {
            var interrupted = await InterruptSaveAsync(root, older);
            var other = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
            var unrelated = Path.Combine(other, Path.GetFileName(interrupted.Path));
            var movedBackup = Path.Combine(other, Path.GetFileName(interrupted.Backup));
            File.Move(interrupted.Backup, movedBackup);
            File.Move(interrupted.Receipt, Path.Combine(other, Path.GetFileName(interrupted.Receipt)));
            var retained = new YMMProjectManagerSettings(unrelated);
            Require(retained.FindInterruptedSave() is null && !retained.SetSearchFolders([newer]) &&
                !File.Exists(unrelated) && File.ReadAllBytes(movedBackup).SequenceEqual(interrupted.Before),
                "Evidence transplanted from another directory was accepted.");
        });
    }

    internal const string InterruptedSaveArgument = "--r29-settings-interrupted-save";
    private const string FixtureFileName = "settings.json";
    private static string ReadyEventName(string root) => @"Local\YmmPmR29Settings-" + Path.GetFileName(root);

    internal static void RunInterruptedSaveChild(string root)
    {
        var fixtureRoot = Path.GetFullPath(root);
        var allowed = Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests");
        var relative = Path.GetRelativePath(allowed, fixtureRoot).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 3 || !Guid.TryParseExact(relative[0], "N", out _) ||
            relative[1] != "r28-settings" || !Guid.TryParseExact(relative[2], "N", out _))
            throw new InvalidOperationException("Settings interruption fixture is outside the test root.");

        using var ready = EventWaitHandle.OpenExisting(ReadyEventName(fixtureRoot));
        using var waitForTermination = new ManualResetEvent(initialState: false);
        var settings = new YMMProjectManagerSettings(Path.Combine(fixtureRoot, FixtureFileName), () =>
        {
            ready.Set();
            if (!waitForTermination.WaitOne(TimeSpan.FromSeconds(40)))
                throw new IOException("The test parent did not terminate its child.");
        });
        settings.SetSearchFolders([fixtureRoot]);
    }

    private static async Task<(string Path, string Backup, string Receipt, byte[] Before, (ulong, Guid) Identity)>
        InterruptSaveAsync(string root, string folder)
    {
        var fixture = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(fixture, FixtureFileName);
        Require(new YMMProjectManagerSettings(path).SetSearchFolders([folder]), "Cannot create synthetic settings.");
        var before = File.ReadAllBytes(path);
        (ulong, Guid) identity;
        using (var original = File.OpenRead(path))
            identity = ProjectFileEvidence.ReadPhysicalIdentity(original);

        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(fixture));
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
        var launch = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            launch.ArgumentList.Add(typeof(Program).Assembly.Location);
        launch.ArgumentList.Add(InterruptedSaveArgument);
        launch.ArgumentList.Add(fixture);
        using var process = Process.Start(launch) ?? throw new InvalidOperationException("Could not start the test child.");
        try
        {
            Require(await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(20))), "Child did not leave the old file parked.");
            Require(!File.Exists(path), "The child did not reach the interruption boundary.");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Require(process.ExitCode != 0, "The child did not terminate abruptly.");
        var backup = Directory.GetFiles(fixture, FixtureFileName + ".previous-*").Single();
        var receipt = Directory.GetFiles(fixture, FixtureFileName + ".recovery-*").Single();
        // Windows can signal process exit before NTFS releases the child's file sharing locks.
        using var released = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitForSharingReleaseAsync(receipt, released.Token);
        await WaitForSharingReleaseAsync(backup, released.Token);
        return (path, backup, receipt, before, identity);
    }

    private static async Task WaitForSharingReleaseAsync(string path, CancellationToken cancellationToken)
    {
        const int sharingViolation = 32;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: true);
                return;
            }
            catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: sharingViolation })
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
