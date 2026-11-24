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
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
