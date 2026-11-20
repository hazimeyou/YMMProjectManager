using System.Diagnostics;
using System.IO;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

internal static class ProjectListPublicationTests
{
    internal static async Task RunAsync(string workRoot)
    {
        foreach (var point in new[] { "before-candidate", "write", "flushed", "published", "externally-replaced", "externally-restored", "missing-receipt", "corrupt-receipt" })
            await Program.RunNamedCaseAsync($"List publication: {point}", () => RunPointAsync(workRoot, point));
    }

    private static async Task RunPointAsync(string workRoot, string point)
    {
        var root = Path.Combine(workRoot, "list-publication", point);
        Directory.CreateDirectory(root);
        var repository = Open(root);
        var original = await repository.LoadAsync();
        original.Projects.Add(new ProjectEntry { ProjectId = Guid.NewGuid(), FullPath = Path.Combine(root, "source.ymmp"), DisplayName = "before" });
        await repository.SaveAsync(original);
        var originalBytes = await File.ReadAllBytesAsync(Path.Combine(root, "projects.json"));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--list-publication-crash");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(point);
        using var child = Process.Start(start) ?? throw new Exception("Crash fixture could not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await child.WaitForExitAsync(timeout.Token);
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        Require(child.ExitCode == 86, $"Child did not stop at {point}.");

        var claim = Directory.EnumerateFiles(repository.RecoveryDirectory, "*.saving").Single();
        var draft = await new ProjectListDrafts(repository.RecoveryDirectory).ReadAsync(claim, claimed: true);
        var receipt = Directory.EnumerateFiles(repository.RecoveryDirectory, "*.publication.json")
            .Single(path => !Path.GetFileName(path).StartsWith("standalone-", StringComparison.Ordinal));
        if (point == "externally-replaced")
        {
            var external = await repository.LoadAsync();
            external.Projects.Single().DisplayName = "external";
            await repository.SaveAsync(external);
        }
        if (point == "externally-restored") await File.WriteAllBytesAsync(Path.Combine(root, "projects.json"), originalBytes);
        if (point == "missing-receipt") File.Move(receipt, receipt + ".fixture-hidden");
        if (point == "corrupt-receipt") await File.WriteAllTextAsync(receipt, "{}");

        var revision = (await repository.LoadAsync()).Revision;
        if (point == "corrupt-receipt")
        {
            try { await repository.ReconcileDraftAsync(claim); throw new Exception("Corrupt receipt was accepted."); }
            catch (InvalidDataException) { }
            Require(File.Exists(claim) && await File.ReadAllTextAsync(receipt) == "{}", "Corrupt evidence was modified.");
        }
        else
        {
            var actual = await repository.ReconcileDraftAsync(claim);
            var expected = point switch
            {
                "published" => ProjectListPublications.Outcome.Committed,
                "before-candidate" or "write" or "flushed" => ProjectListPublications.Outcome.Unpublished,
                _ => ProjectListPublications.Outcome.Unknown,
            };
            Require(actual == expected, $"Unexpected outcome for {point}: {actual}.");
            if (actual == ProjectListPublications.Outcome.Unknown)
            {
                Require(File.Exists(claim) && await repository.ReconcileDraftAsync(claim) == actual, "Uncertain evidence was not preserved.");
            }
            else
            {
                Require(!File.Exists(claim) && File.Exists(claim[..^7] + (actual == ProjectListPublications.Outcome.Committed ? ".closed" : "")),
                    "Recovery record moved to the wrong state.");
                try { await repository.ReconcileDraftAsync(claim); throw new Exception("Old claim was reused."); }
                catch (FileNotFoundException) { }
            }
        }
        Require((await repository.LoadAsync()).Revision == revision, "Reconciliation wrote the project list.");
        if (point is "before-candidate" or "write" or "flushed") Require(revision == original.Revision, "Unpublished output replaced the base.");
        if (point == "published") Require((await repository.LoadAsync()).Projects.Single().DisplayName == draft.Local.Projects.Single().DisplayName,
            "Committed list did not contain the planned value.");
    }

    internal static async Task CrashAsync(string root, string point)
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests")) + Path.DirectorySeparatorChar;
        root = Path.GetFullPath(root);
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture must be isolated.");
        var repository = Open(root);
        var baseline = await repository.LoadAsync();
        var candidate = await repository.LoadAsync();
        candidate.Projects.Single().DisplayName = "after";
        var drafts = new ProjectListDrafts(repository.RecoveryDirectory);
        var claim = drafts.Claim(await drafts.WriteAsync(baseline, candidate, []));
        var crash = new JsonProjectRepository(new FileLogger(Path.Combine(root, "crash.log")), Path.Combine(root, "projects.json"),
            writeTemporaryFile: point == "write" ? async (file, bytes, token) =>
            {
                await file.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), token);
                Environment.Exit(86);
            } : null,
            beforeReplace: point == "flushed" ? () => Environment.Exit(86) : null,
            afterReplace: point is not ("before-candidate" or "write" or "flushed") ? () => Environment.Exit(86) : null,
            beforeCandidate: point == "before-candidate" ? () => Environment.Exit(86) : null);
        await crash.SaveWithDraftAsync(candidate, claim);
        throw new Exception("Crash hook was not executed.");
    }

    private static JsonProjectRepository Open(string root) => new(new FileLogger(Path.Combine(root, "test.log")), Path.Combine(root, "projects.json"));
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
