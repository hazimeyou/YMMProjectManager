using System.Diagnostics;
using System.IO;
using YMMProjectManager.Application;
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
        await Program.RunNamedCaseAsync("List draft retirement cannot race a generation or list publication",
            () => TestRetirementDuringPublicationAsync(workRoot));
        await Program.RunNamedCaseAsync("List publication holds an in-place write lease through replacement",
            () => TestListLeaseDuringPublicationAsync(workRoot));
        foreach (var point in new[] { "before-park", "parked", "before-cleanup" })
            await Program.RunNamedCaseAsync($"Interrupted list replacement: {point}", () => TestInterruptedReplacementAsync(workRoot, point));
        foreach (var point in new[] { "external-before-park", "external-after-park" })
            await Program.RunNamedCaseAsync($"Independent list replacement: {point}", () => TestExternalReplacementAsync(workRoot, point));
    }

    private static async Task TestInterruptedReplacementAsync(string workRoot, string point)
    {
        var root = Path.Combine(workRoot, "list-replacement-crash", point);
        var (repository, path, original, originalBytes) = await ReplacementFixtureAsync(root);
        await RunReplacementChildAsync(root, point, 86);
        Require(repository.HasInterruptedPublication, "Interruption lost its durable journal.");
        var evidence = await repository.InspectInterruptedPublicationAsync() ?? throw new Exception("Missing recovery evidence.");
        var claim = Directory.GetFiles(repository.RecoveryDirectory, "*.saving").Single();
        var wanted = point switch
        {
            "before-park" => ProjectListReplacement.RecoveryState.ActivePreserved,
            "parked" => ProjectListReplacement.RecoveryState.CanRestore,
            "before-cleanup" => ProjectListReplacement.RecoveryState.Published,
            _ => throw new ArgumentOutOfRangeException(nameof(point)),
        };
        Require(evidence.State == wanted, $"Unexpected replacement state: {evidence.State}.");
        await ExpectRejectedAsync(() => repository.SaveAsync(original));
        if (wanted == ProjectListReplacement.RecoveryState.CanRestore)
        {
            Require(!File.Exists(path), "The crash fixture did not leave the gap.");
            await ExpectRejectedAsync(() => repository.LoadAsync());
        }
        await repository.RecoverInterruptedPublicationAsync(evidence);
        Require(!repository.HasInterruptedPublication, "Resolved replacement still blocks publication.");
        var committed = wanted == ProjectListReplacement.RecoveryState.Published;
        var outcome = await repository.ReconcileDraftAsync(claim);
        Require(outcome == (committed ? ProjectListPublications.Outcome.Committed : ProjectListPublications.Outcome.Unpublished),
            "Draft receipt did not reconcile the replacement outcome.");
        if (committed)
        {
            Require((await repository.LoadAsync()).Projects.Single().DisplayName == "after", "The committed list was replaced during recovery.");
            Require(Enumerable.SequenceEqual(originalBytes, await File.ReadAllBytesAsync(evidence.ParkedPath)), "The old published version was lost.");
        }
        else
        {
            Require(Enumerable.SequenceEqual(originalBytes, await File.ReadAllBytesAsync(path)), "The exact baseline did not survive recovery.");
            var ready = repository.GetReadyDraftPath(claim);
            var local = (await new ProjectListDrafts(repository.RecoveryDirectory).ReadAsync(ready)).Local;
            await repository.SaveReadyDraftAsync(local, ready);
            Require((await repository.LoadAsync()).Projects.Single().DisplayName == "after", "The released draft could not be reapplied.");
        }
    }

    private static async Task TestExternalReplacementAsync(string workRoot, string point)
    {
        var root = Path.Combine(workRoot, "list-external-replacement", point);
        var (repository, path, _, originalBytes) = await ReplacementFixtureAsync(root);
        var external = """{"Projects":[],"Folders":[],"UnrecognizedExternalField":{"keep":42}}""";
        await File.WriteAllTextAsync(Path.Combine(root, "synthetic-foreign.json"), external);
        var local = await repository.LoadAsync();
        local.Projects.Single().DisplayName = "local";
        var replaced = point == "external-before-park";
        Action externalWrite = () => RunReplacementChildAsync(root, point, 0).GetAwaiter().GetResult();
        var publisher = new JsonProjectRepository(new FileLogger(Path.Combine(root, "publisher.log")), path,
            afterRevisionCheck: replaced ? externalWrite : null, afterParking: replaced ? null : externalWrite);
        try { await publisher.SaveAsync(local); throw new Exception("Publication accepted an externally replaced active list."); }
        catch (IOException) { }

        var evidence = await repository.InspectInterruptedPublicationAsync() ?? throw new Exception("Missing collision evidence.");
        Require(File.Exists(evidence.CandidatePath), "Unpublished application version was discarded.");
        var candidate = JsonProjectRepository.DeserializeStore(await File.ReadAllBytesAsync(evidence.CandidatePath));
        Require(candidate.Projects.Single().DisplayName == "local", "Application candidate contents changed.");
        if (replaced)
        {
            Require(evidence.State == ProjectListReplacement.RecoveryState.Unverifiable && !File.Exists(path),
                "The externally replaced file was treated as the checked original.");
            Require(await File.ReadAllTextAsync(evidence.ParkedPath) == external, "The exact external bytes were not saved.");
            Require(Enumerable.SequenceEqual(originalBytes, await File.ReadAllBytesAsync(Path.Combine(root, "synthetic-prior.json"))),
                "Original source was changed.");
            await ExpectRejectedAsync(() => repository.RecoverInterruptedPublicationAsync(evidence));
            await ExpectRejectedAsync(() => repository.LoadAsync());
            return;
        }
        Require(evidence.State == ProjectListReplacement.RecoveryState.Conflict && await File.ReadAllTextAsync(path) == external,
            "The newly created external active list was overwritten.");
        Require(Enumerable.SequenceEqual(originalBytes, await File.ReadAllBytesAsync(evidence.ParkedPath)), "The original parked bytes were lost.");
        await File.WriteAllTextAsync(path, external.Replace("42", "43", StringComparison.Ordinal));
        await ExpectRejectedAsync(() => repository.RecoverInterruptedPublicationAsync(evidence));
        var latest = await repository.InspectInterruptedPublicationAsync() ?? throw new Exception("Collision evidence was not retained.");
        await repository.RecoverInterruptedPublicationAsync(latest);
        Require(await File.ReadAllTextAsync(path) == external.Replace("42", "43", StringComparison.Ordinal) &&
            File.Exists(latest.CandidatePath) && File.Exists(latest.ParkedPath),
            "Explicit conflict acknowledgment changed a version or discarded evidence.");
    }

    private static async Task<(JsonProjectRepository Repository, string Path, ProjectStore Store, byte[] Bytes)> ReplacementFixtureAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "projects.json");
        var repository = Open(root);
        var baseline = await repository.LoadAsync();
        baseline.Projects.Add(new ProjectEntry { ProjectId = Guid.NewGuid(), FullPath = Path.Combine(root, "source.ymmp"), DisplayName = "before" });
        await repository.SaveAsync(baseline);
        return (repository, path, baseline, await File.ReadAllBytesAsync(path));
    }

    private static async Task RunReplacementChildAsync(string root, string point, int exitCode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--list-publication-crash");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(point);
        using var child = Process.Start(start) ?? throw new Exception("Replacement fixture could not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await child.WaitForExitAsync(timeout.Token);
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        Require(child.ExitCode == exitCode, $"Replacement child exit {child.ExitCode}; expected {exitCode} at {point}.");
    }

    private static async Task TestListLeaseDuringPublicationAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "list-in-place-lease");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "projects.json");
        var repository = Open(root);
        var store = await repository.LoadAsync();
        store.Projects.Add(new ProjectEntry { ProjectId = Guid.NewGuid(), FullPath = Path.Combine(root, "source.ymmp"), DisplayName = "original" });
        await repository.SaveAsync(store);
        var writerRefused = false;
        var leasedPublisher = new JsonProjectRepository(new FileLogger(Path.Combine(root, "lease.log")), path,
            afterRevisionCheck: () =>
            {
                try { File.WriteAllText(path, "competing in-place write"); }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32) { writerRefused = true; }
            });

        store.Projects.Single().DisplayName = "published";
        await leasedPublisher.SaveAsync(store);
        Require(writerRefused && (await repository.LoadAsync()).Projects.Single().DisplayName == "published",
            "An in-place writer reached the checked list, or the lease prevented its legitimate replacement.");
    }

    private static async Task TestRetirementDuringPublicationAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "list-retirement-lock");
        Directory.CreateDirectory(root);
        var repository = Open(root);
        var baseline = await repository.LoadAsync();
        var local = await repository.LoadAsync();
        local.Projects.Add(new ProjectEntry { ProjectId = Guid.NewGuid(), FullPath = Path.Combine(root, "source.ymmp") });
        var previous = await repository.WritePendingDraftAsync(baseline, local, [], null);

        async Task<T> PublishWithConcurrentRetirementAsync<T>(string ready, Func<Action, Task<T>> publish)
        {
            var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resume = new ManualResetEventSlim();
            var running = Task.Run(() => publish(() =>
            {
                acquired.SetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Publication fixture was not released.");
            }));
            try
            {
                await acquired.Task.WaitAsync(TimeSpan.FromSeconds(15));
                try
                {
                    await Open(root).RetireReadyDraftAsync(ready);
                    throw new Exception("Retirement acquired a draft already being published.");
                }
                catch (ProjectListBusyException) { }
                Require(File.Exists(ready) && !File.Exists(ready + ".closed"), "Busy retirement changed the ready draft.");
            }
            finally { resume.Set(); }
            return await running;
        }

        var successor = await PublishWithConcurrentRetirementAsync(previous,
            lockHeld => repository.WritePendingDraftAsync(baseline, local, [], previous, beforePublication: lockHeld));
        await ExpectRejectedAsync(() => repository.RetireReadyDraftAsync(previous));
        Require(File.Exists(previous), "Stale retirement archived the predecessor.");
        await PublishWithConcurrentRetirementAsync(successor, async lockHeld =>
        {
            var publisher = new JsonProjectRepository(new FileLogger(Path.Combine(root, "publisher.log")),
                Path.Combine(root, "projects.json"), afterReceipt: lockHeld);
            await publisher.SaveReadyDraftAsync(local, successor);
            return true;
        });
        Require(File.Exists(successor + ".closed") && !File.Exists(successor) &&
            (await repository.LoadAsync()).Projects.Count == 1, "Contention changed the successful publication outcome.");
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
        ProjectListDrafts.CheckOrdinaryDirectory(root);
        if (point is "external-before-park" or "external-after-park")
        {
            if (Path.GetFileName(Path.GetDirectoryName(root)) != "list-external-replacement")
                throw new InvalidOperationException("External replacement fixture must be isolated.");
            var active = Path.Combine(root, "projects.json");
            if (point == "external-before-park") File.Move(active, Path.Combine(root, "synthetic-prior.json"), overwrite: false);
            File.Move(Path.Combine(root, "synthetic-foreign.json"), active, overwrite: false);
            return;
        }
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
            duringReceipt: point == "during-receipt" ? () => Environment.Exit(86) : null,
            afterRevisionCheck: point == "before-park" ? () => Environment.Exit(86) : null,
            afterParking: point == "parked" ? () => Environment.Exit(86) : null,
            beforeJournalCleanup: point == "before-cleanup" ? () => Environment.Exit(86) : null);
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
