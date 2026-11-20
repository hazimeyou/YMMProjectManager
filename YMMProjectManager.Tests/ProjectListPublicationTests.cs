using System.Diagnostics;
using System.IO;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

internal static class ProjectListPublicationTests
{
    internal static async Task RunAsync(string workRoot)
    {
        foreach (var point in new[]
        {
            "before-receipt", "during-receipt", "after-receipt", "after-claim", "before-candidate", "write", "flushed", "published",
            "fresh-attempt", "later-saves", "staged-then-external-edit", "externally-restored", "raw-edit-after-later-save",
            "missing-descendant", "corrupt-descendant", "duplicate-descendant", "missing-receipt", "corrupt-receipt", "corrupt-claim", "legacy-claim",
        })
            await Program.RunNamedCaseAsync($"List publication: {point}", () => RunPointAsync(workRoot, point));
        foreach (var point in new[] { "before-generation", "after-generation", "ambiguous-generation", "missing-intermediate-generation" })
            await Program.RunNamedCaseAsync($"List draft generation: {point}", () => RunGenerationPointAsync(workRoot, point));
    }

    private static async Task RunGenerationPointAsync(string workRoot, string point)
    {
        var root = Path.Combine(workRoot, "list-generation", point);
        Directory.CreateDirectory(root);
        var repository = Open(root);
        var baseline = await repository.LoadAsync();
        baseline.Projects.Add(new ProjectEntry { ProjectId = Guid.NewGuid(), FullPath = Path.Combine(root, "source.ymmp"), DisplayName = "base" });
        await repository.SaveAsync(baseline);
        var original = await File.ReadAllBytesAsync(Path.Combine(root, "projects.json"));
        var older = await repository.LoadAsync();
        older.Projects.Single().DisplayName = "older";
        var previous = await repository.WritePendingDraftAsync(baseline, older, [], null);
        var priorBytes = await File.ReadAllBytesAsync(previous);

        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--list-publication-crash");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(point);
        using var child = Process.Start(start) ?? throw new Exception("Generation interruption fixture could not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await child.WaitForExitAsync(timeout.Token);
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        Require(child.ExitCode == 86, "Child never reached the generation interruption.");
        Require(Enumerable.SequenceEqual(priorBytes, await File.ReadAllBytesAsync(previous)), "The predecessor changed during generation transition.");
        Require(Enumerable.SequenceEqual(original, await File.ReadAllBytesAsync(Path.Combine(root, "projects.json"))), "A draft transition wrote the list.");

        var drafts = new ProjectListDrafts(repository.RecoveryDirectory);
        var (review, superseded) = await drafts.ReadForReviewAsync(previous);
        Require(review.Local.Projects.Single().DisplayName == "older", "The old draft cannot be exported.");
        if (point == "before-generation")
        {
            Require(!superseded, "An uncommitted temporary draft fenced its predecessor.");
            Require(Directory.GetFiles(repository.RecoveryDirectory, "*.draft.json").Single() == previous,
                "The interrupted write created a usable successor.");
            await repository.SaveReadyDraftAsync(review.Local, previous);
            Require((await repository.LoadAsync()).Projects.Single().DisplayName == "older" && File.Exists(previous + ".closed"),
                "The predecessor could not be saved after an interrupted generation write.");
            return;
        }

        Require(superseded, "A published successor did not fence its predecessor.");
        await ExpectRejectedAsync(() => repository.SaveReadyDraftAsync(review.Local, previous));
        var successor = Directory.GetFiles(repository.RecoveryDirectory, "*.draft.json").Single(path => path != previous);
        var next = await drafts.ReadAsync(successor);
        Require(next.Local.Projects.Single().DisplayName == "newer", "The published successor did not retain the newer edits.");
        await ExpectRejectedAsync(() => repository.WritePendingDraftAsync(baseline, older, [], previous));

        if (point == "ambiguous-generation")
        {
            var broken = Path.Combine(repository.RecoveryDirectory, $"{Guid.NewGuid():N}.{Path.GetFileName(previous)[..32]}.1.draft.json");
            await File.WriteAllTextAsync(broken, "unknown future format");
            var (_, exportOnly) = await drafts.ReadForReviewAsync(successor);
            Require(exportOnly, "A peer with unsupported data did not block the competing generation.");
            await ExpectRejectedAsync(() => repository.SaveReadyDraftAsync(next.Local, successor));
            Require(await File.ReadAllTextAsync(broken) == "unknown future format" &&
                Enumerable.SequenceEqual(original, await File.ReadAllBytesAsync(Path.Combine(root, "projects.json"))),
                "An ambiguous transition lost evidence or wrote the list.");
            return;
        }

        if (point == "missing-intermediate-generation")
        {
            next.Local.Projects.Single().DisplayName = "newest";
            var last = await repository.WritePendingDraftAsync(baseline, next.Local, [], successor);
            var detached = Path.Combine(root, "synthetic-detached-generation");
            File.Move(successor, detached);
            await ExpectRejectedAsync(() => repository.SaveReadyDraftAsync(review.Local, previous));
            var latest = await drafts.ReadAsync(last);
            await repository.SaveReadyDraftAsync(latest.Local, last);
            Require(File.Exists(detached) && File.Exists(last + ".closed") &&
                (await repository.LoadAsync()).Projects.Single().DisplayName == "newest",
                "Missing intermediate evidence caused an older replay or erased the newest generation.");
            return;
        }

        await repository.SaveReadyDraftAsync(next.Local, successor);
        Require(File.Exists(successor + ".closed") && !File.Exists(successor) &&
            (await repository.LoadAsync()).Projects.Single().DisplayName == "newer", "The successor could not be saved and retired.");
        await ExpectRejectedAsync(() => repository.SaveReadyDraftAsync(review.Local, previous));
        await File.WriteAllTextAsync(successor + ".closed", "unsupported archived evidence");
        await ExpectRejectedAsync(() => repository.SaveReadyDraftAsync(review.Local, previous));
        drafts.Retire(previous);
        Require(Enumerable.SequenceEqual(priorBytes, await File.ReadAllBytesAsync(previous + ".closed")), "Legacy retirement did not preserve the original bytes.");
    }

    private static async Task ExpectRejectedAsync(Func<Task> run)
    {
        try { await run(); throw new Exception("An obsolete or ambiguous draft could be replayed."); }
        catch (InvalidDataException) { }
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

        if (point is "before-receipt" or "during-receipt" or "after-receipt")
        {
            Require(!Directory.EnumerateFiles(repository.RecoveryDirectory, "*.saving").Any(), "Claim was made before receipt became durable.");
            var ready = Directory.EnumerateFiles(repository.RecoveryDirectory, "*.draft.json").Single();
            var retry = await new ProjectListDrafts(repository.RecoveryDirectory).ReadAsync(ready);
            Require((await repository.LoadAsync()).Revision == original.Revision, "Unclaimed draft changed the list.");
            await repository.SaveReadyDraftAsync(retry.Local, ready);
            Require(File.Exists(ready + ".closed") && (await repository.LoadAsync()).Projects.Single().DisplayName == "after",
                "Ready draft could not be saved after an interrupted receipt.");
            return;
        }

        var claim = Directory.EnumerateFiles(repository.RecoveryDirectory, "*.saving").Single();
        var drafts = new ProjectListDrafts(repository.RecoveryDirectory);
        var (draftId, operation) = drafts.GetClaimedIdentity(claim);
        var draft = await drafts.ReadAsync(claim, claimed: true);
        Require((point == "legacy-claim") == (operation is null), "The new claim did not bind exactly one operation.");
        var receipt = Directory.EnumerateFiles(repository.RecoveryDirectory, "*.publication.json")
            .Single(path => Path.GetFileName(path).StartsWith($"{draftId:N}-", StringComparison.Ordinal) &&
                (operation is null || Path.GetFileName(path).Contains($"{operation:N}", StringComparison.Ordinal)));
        if (point is "later-saves" or "staged-then-external-edit" or "raw-edit-after-later-save" or
            "missing-descendant" or "corrupt-descendant" or "duplicate-descendant")
        {
            var external = await repository.LoadAsync();
            external.Projects.Single().DisplayName = "external";
            await repository.SaveAsync(external);
            external.Projects.Single().DisplayName = "second later save";
            await repository.SaveAsync(external);
            var latestReceipt = Path.Combine(repository.RecoveryDirectory, $"standalone-{external.Revision!.OperationId:N}.publication.json");
            if (point == "missing-descendant") File.Move(latestReceipt, latestReceipt + ".fixture-hidden");
            if (point == "corrupt-descendant") await File.WriteAllTextAsync(latestReceipt, "{}");
            if (point == "duplicate-descendant")
                File.Copy(latestReceipt, Path.Combine(repository.RecoveryDirectory, $"{Guid.NewGuid():N}-{external.Revision.OperationId:N}.publication.json"));
            if (point == "raw-edit-after-later-save")
            {
                var destination = Path.Combine(root, "projects.json");
                var raw = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(destination))!;
                raw["ExternalExtra"] = "newer raw contents";
                await File.WriteAllTextAsync(destination, raw.ToJsonString());
            }
        }
        if (point == "externally-restored") await File.WriteAllBytesAsync(Path.Combine(root, "projects.json"), originalBytes);
        if (point == "missing-receipt") File.Move(receipt, receipt + ".fixture-hidden");
        if (point == "corrupt-receipt") await File.WriteAllTextAsync(receipt, "{}");
        if (point == "corrupt-claim") await File.WriteAllTextAsync(claim, "{}");

        var revision = (await repository.LoadAsync()).Revision;
        if (point is "corrupt-receipt" or "corrupt-claim")
        {
            try { await repository.ReconcileDraftAsync(claim); throw new Exception("Corrupt receipt was accepted."); }
            catch (InvalidDataException) { }
            Require(File.Exists(claim) && await File.ReadAllTextAsync(point == "corrupt-claim" ? claim : receipt) == "{}",
                "Corrupt evidence was modified.");
        }
        else
        {
            var actual = await repository.ReconcileDraftAsync(claim);
            var expected = point switch
            {
                "published" or "legacy-claim" or "later-saves" => ProjectListPublications.Outcome.Committed,
                "after-claim" or "fresh-attempt" or "before-candidate" or "write" or "flushed" or
                    "staged-then-external-edit" => ProjectListPublications.Outcome.Unpublished,
                _ => ProjectListPublications.Outcome.Unknown,
            };
            Require(actual == expected, $"Unexpected outcome for {point}: {actual}.");
            if (actual == ProjectListPublications.Outcome.Unknown)
            {
                Require(File.Exists(claim) && await repository.ReconcileDraftAsync(claim) == actual, "Uncertain evidence was not preserved.");
            }
            else
            {
                var ready = repository.GetReadyDraftPath(claim);
                Require(!File.Exists(claim) && File.Exists(ready + (actual == ProjectListPublications.Outcome.Committed ? ".closed" : "")),
                    "Recovery record moved to the wrong state.");
                try { await repository.ReconcileDraftAsync(claim); throw new Exception("Old claim was reused."); }
                catch (FileNotFoundException) { }
            }
        }
        Require((await repository.LoadAsync()).Revision == revision, "Reconciliation wrote the project list.");
        if (point is "after-claim" or "fresh-attempt" or "before-candidate" or "write" or "flushed")
            Require(revision == original.Revision, "Unpublished output replaced the base.");
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
        if (point is "before-generation" or "after-generation" or "ambiguous-generation" or "missing-intermediate-generation")
        {
            var previous = Directory.GetFiles(repository.RecoveryDirectory, "*.draft.json").Single();
            candidate.Projects.Single().DisplayName = "newer";
            await repository.WritePendingDraftAsync(baseline, candidate, [], previous,
                beforePublication: point == "before-generation" ? () => Environment.Exit(86) : null,
                afterPublication: () => Environment.Exit(point == "before-generation" ? 87 : 86));
            throw new Exception("Generation fault hook returned unexpectedly.");
        }
        var ready = await drafts.WriteAsync(baseline, candidate, []);
        if (point == "fresh-attempt")
        {
            var publications = new ProjectListPublications(repository.RecoveryDirectory);
            var receipt = new ProjectListPublications.Receipt(1, Guid.NewGuid(), drafts.GetReadyId(ready),
                new string('1', 64), new string('2', 64), false);
            await publications.WriteAsync(receipt, default);
            await publications.WriteAsync(receipt with { PublicationStarted = true }, default);
        }
        var crash = new JsonProjectRepository(new FileLogger(Path.Combine(root, "crash.log")), Path.Combine(root, "projects.json"),
            writeTemporaryFile: point == "write" ? async (file, bytes, token) =>
            {
                await file.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), token);
                Environment.Exit(86);
            } : null,
            beforeReplace: point == "flushed" ? () => Environment.Exit(86) : null,
            afterReplace: point == "legacy-claim" ? null : () => Environment.Exit(86),
            beforeCandidate: point == "before-candidate" ? () => Environment.Exit(86) : null,
            beforeReceipt: point == "before-receipt" ? () => Environment.Exit(86) : null,
            afterReceipt: point == "after-receipt" ? () => Environment.Exit(86) : null,
            afterDraftClaim: point is "after-claim" or "fresh-attempt" or "staged-then-external-edit" ? () => Environment.Exit(86) : null,
            duringReceipt: point == "during-receipt" ? () => Environment.Exit(86) : null);
        if (point == "legacy-claim")
        {
            // R14 published .saving without an operation suffix.
            var legacyClaim = ready + ".saving";
            File.Move(ready, legacyClaim, overwrite: false);
            await crash.SaveAsync(candidate);
            var legacyPublications = new ProjectListPublications(repository.RecoveryDirectory);
            var legacyReceipt = new ProjectListPublications.Receipt(1, candidate.Revision!.OperationId!.Value,
                drafts.GetClaimedIdentity(legacyClaim).Id, baseline.Revision!.Sha256, candidate.Revision.Sha256!, PublicationStarted: false);
            await legacyPublications.WriteAsync(legacyReceipt, default);
            await legacyPublications.WriteAsync(legacyReceipt with { PublicationStarted = true }, default);
            Environment.Exit(86);
        }
        await crash.SaveReadyDraftAsync(candidate, ready);
        throw new Exception("Crash hook was not executed.");
    }

    private static JsonProjectRepository Open(string root) => new(new FileLogger(Path.Combine(root, "test.log")), Path.Combine(root, "projects.json"));
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
