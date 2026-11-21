using System.IO;
using System.Security.Cryptography;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;
using YMMProjectManager.Presentation.ViewModels;

internal static class DerivedRelinkAssociationTests
{
    internal static Task RunAsync(string workRoot) => Program.WpfTestHost.RunAsync(async () =>
    {
        await Program.RunNamedCaseAsync("R22 derived registration keeps both identities across failed list save and repeated recovery", async () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "derived-ui")).FullName;
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var list = Path.Combine(root, "projects.json");
            var fixture = await FixtureAsync(root);
            var shouldFail = false;
            var repository = new JsonProjectRepository(logger, list, beforeReplace: () =>
            {
                if (shouldFail) throw new IOException("synthetic derived list publication failure");
            });
            var store = await repository.LoadAsync();
            store.Projects.Add(fixture.Source);
            await repository.SaveAsync(store);
            var view = new ProjectListViewModel(logger, repository);
            await view.InitializeAsync();
            var source = view.Projects.Single();
            var baseline = await File.ReadAllBytesAsync(list);
            shouldFail = true;
            try
            {
                await view.ApplyDerivedRelinkAsync(source, fixture.Association, fixture.Receipt, default);
                throw new InvalidOperationException("The list failure was not reported.");
            }
            catch (IOException ex) when (ex.Message == "synthetic derived list publication failure") { }
            var afterFailure = await File.ReadAllBytesAsync(list);
            Check(baseline.SequenceEqual(afterFailure) && !view.HasUnsavedProjectList && view.Projects.Count == 1 &&
                (!Directory.Exists(repository.RecoveryDirectory) || !Directory.GetFiles(repository.RecoveryDirectory, "*.draft.json").Any()),
                "The interrupted list save exposed a draft that could bypass strict relink recovery.");
            await view.RetryProjectListSaveAsync();
            Check(view.Projects.Count == 1 && (await repository.LoadAsync()).Projects.Count == 1,
                "Generic list retry registered a derivation without the relink protocol.");

            shouldFail = false;
            await view.ApplyDerivedRelinkAsync(source, fixture.Association, fixture.Receipt, default);
            var saved = (await repository.LoadAsync()).Projects;
            var original = saved.Single(item => item.ProjectId == fixture.Source.ProjectId);
            var derived = saved.Single(item => item.ProjectId == fixture.Receipt.Output.Revision.ProjectId);
            Check(original.FullPath == fixture.Source.FullPath && original.MetadataAssociation == fixture.Association &&
                original.FileIdentity == fixture.Source.FileIdentity && original.LinkedYmmpFiles.Single().Role == YmmpRole.Main,
                "Derived registration changed the original entry.");
            Check(derived.FullPath == fixture.Receipt.Output.Output && derived.MetadataAssociation?.Directory == fixture.Receipt.SeriesDirectory &&
                derived.MetadataAssociation.SelectedRevisionId == fixture.Receipt.Output.Revision.RevisionId &&
                derived.FileIdentity?.Sha256 == fixture.Receipt.Output.OutputSha256 && !view.HasUnsavedProjectList,
                "The persisted derived entry has the wrong metadata or physical identity.");
            var published = await File.ReadAllBytesAsync(list);
            await view.ApplyDerivedRelinkAsync(source, fixture.Association, fixture.Receipt, default);
            var afterRetry = await File.ReadAllBytesAsync(list);
            Check(published.SequenceEqual(afterRetry) && view.Projects.Count == 2, "Repeated recovery rewrote or duplicated the list.");
            var external = await repository.LoadAsync();
            external.Projects.RemoveAll(item => item.ProjectId == fixture.Receipt.Output.Revision.ProjectId);
            await repository.SaveAsync(external);
            await RejectAsync(() => view.ApplyDerivedRelinkAsync(source, fixture.Association, fixture.Receipt, default));
            Check((await repository.LoadAsync()).Projects.Count == 1, "Retry overwrote a concurrent deletion from the saved list.");
        });

        await Program.RunNamedCaseAsync("R22 exact published list is recovered without a second write after an uncertain return", async () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "derived-list-unknown")).FullName;
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var list = Path.Combine(root, "list.json");
            var fixture = await FixtureAsync(root);
            var failAfterPublish = false;
            var repository = new JsonProjectRepository(logger, list, afterReplace: () =>
            {
                if (failAfterPublish) throw new IOException("synthetic exception after publishing the derived list");
            });
            var store = await repository.LoadAsync();
            store.Projects.Add(fixture.Source);
            await repository.SaveAsync(store);
            var view = new ProjectListViewModel(logger, repository);
            await view.InitializeAsync();
            var source = view.Projects.Single();
            failAfterPublish = true;
            try
            {
                await view.ApplyDerivedRelinkAsync(source, fixture.Association, fixture.Receipt, default);
                throw new InvalidOperationException("Post-publication exception was lost.");
            }
            catch (IOException ex) when (ex.Message == "synthetic exception after publishing the derived list") { }
            Check(view.Projects.Count == 1 && (await repository.LoadAsync()).Projects.Count == 2,
                "Fixture did not observe published disk state and unchanged in-memory state.");
            var published = await File.ReadAllBytesAsync(list);
            await view.ApplyDerivedRelinkAsync(source, fixture.Association, fixture.Receipt, default);
            var after = await File.ReadAllBytesAsync(list);
            Check(published.SequenceEqual(after) && view.Projects.Count == 2,
                "Recovery attempted to write a second registration instead of adopting the verified output.");
        });

        await Program.RunNamedCaseAsync("R22 derived registration refuses an occupied registered output", async () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "derived-list-collision")).FullName;
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var fixture = await FixtureAsync(root);
            var list = Path.Combine(root, "projects.json");
            var repository = new JsonProjectRepository(logger, list);
            var store = await repository.LoadAsync();
            store.Projects.Add(fixture.Source);
            store.Projects.Add(new ProjectEntry
            {
                FullPath = fixture.Receipt.Output.Output,
                ProjectId = Guid.NewGuid(),
                FileIdentity = await ProjectFileEvidence.ReadAsync(fixture.Receipt.Output.Output),
            });
            await repository.SaveAsync(store);
            var view = new ProjectListViewModel(logger, repository);
            await view.InitializeAsync();
            var before = SHA256.HashData(await File.ReadAllBytesAsync(list));
            await RejectAsync(() => view.ApplyDerivedRelinkAsync(view.Projects.Single(item => item.ProjectId == fixture.Source.ProjectId),
                fixture.Association, fixture.Receipt, default));
            var after = SHA256.HashData(await File.ReadAllBytesAsync(list));
            Check(before.SequenceEqual(after) && view.Projects.Count == 2 && !view.HasUnsavedProjectList,
                "A collision changed the source or existing registration.");
        });

        await Program.RunNamedCaseAsync("R22 complete external relink registers the exact output and child history through the product callback", async () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "derived-e2e")).FullName;
            var projectDirectory = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var approvedRoot = Directory.CreateDirectory(Path.Combine(root, "approved")).FullName;
            var history = Directory.CreateDirectory(Path.Combine(root, "history")).FullName;
            var source = Path.Combine(projectDirectory, "source.ymmp");
            var output = Path.Combine(projectDirectory, "derived.ymmp");
            var media = Path.Combine(approvedRoot, "clip.wav");
            const string original = """{"Items":[{"FilePath":"clip.wav","Unused":123}],"Other":"preserve"}""";
            await File.WriteAllTextAsync(source, original);
            var bytes = "synthetic external source"u8.ToArray();
            await File.WriteAllBytesAsync(media, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var head = await AssetRevisionSeries.InitializeAsync(history, AssetManifestStore.Create(source,
                [new AssetMetadata("clip.wav", bytes.Length, hash, null)], DateTimeOffset.UtcNow));
            var association = new ProjectMetadataAssociation(history, head.RevisionId, head.Sha256, head.RevisionId, head.Sha256,
                head.RevisionId, head.Sha256);
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            var repository = new JsonProjectRepository(logger, Path.Combine(root, "list.json"));
            var store = await repository.LoadAsync();
            store.Projects.Add(new ProjectEntry
            {
                FullPath = source, ProjectId = head.ProjectId, MetadataAssociation = association,
                FileIdentity = await ProjectFileEvidence.ReadAsync(source),
            });
            await repository.SaveAsync(store);
            var view = new ProjectListViewModel(logger, repository);
            await view.InitializeAsync();
            var sourceEntry = view.Projects.Single();
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var scan = await new RelinkScanService(logger).ScanForStrictRelinkAsync(source, default);
            var context = scan.Context ?? throw new InvalidOperationException("External PF fixture could not be read.");
            var row = context.Rows.Single();
            var match = (await AssetPool.FindAsync("clip.wav", hash, [approvedRoot])).Single();
            row.Status = RelinkStatus.Updated;
            row.SelectedCandidate = match.CandidatePath;
            var record = await AssetRelinkRecovery.PrepareExternalOutputAsync(context, output, history, head, head, [head],
                [new AssetRelinkRecovery.StrictAssetProof(row.RowIndex, "clip.wav", match.ManifestPath, match.ManifestSha256)], [approvedRoot], default);
            var staged = await AssetRelinkRecovery.StageExternalOutputAsync(record, context, new RelinkSaveService(logger), [approvedRoot], default);
            Check(staged.Success && !File.Exists(output), "A staged output was unexpectedly visible.");
            var published = await AssetRelinkRecovery.CompleteExternalOutputAsync(record, history, head, source, [approvedRoot], default,
                (receipt, token) => dispatcher.InvokeAsync(() => view.ApplyDerivedRelinkAsync(sourceEntry, association, receipt, token)).Task.Unwrap());
            var listed = (await repository.LoadAsync()).Projects;
            Check(listed.Count == 2 && listed.Single(entry => entry.ProjectId == head.ProjectId).MetadataAssociation == association &&
                listed.Single(entry => entry.ProjectId == published.Output.Revision.ProjectId).FullPath == output &&
                await AssetRevisionSeries.ReadHeadAsync(published.SeriesDirectory) == published.Output.Revision &&
                await AssetRevisionSeries.ReadHeadAsync(history) == head && await File.ReadAllTextAsync(source) == original &&
                (await File.ReadAllTextAsync(output)).Contains("preserve"), "The verified output and derived series were not integrated.");
        });
        return true;
    });

    private static async Task<(ProjectEntry Source, ProjectMetadataAssociation Association, AssetRelinkRecovery.DerivedOutputReceipt Receipt)>
        FixtureAsync(string root)
    {
        var originalPath = Path.Combine(root, "source.ymmp");
        var outputPath = Path.Combine(root, "output.ymmp");
        await File.WriteAllTextAsync(originalPath, """{"Items":[{"FilePath":"old.wav"}]}""");
        await File.WriteAllTextAsync(outputPath, """{"Items":[{"FilePath":"replacement.wav"}]}""");
        var sourceSeries = Directory.CreateDirectory(Path.Combine(root, "source-history")).FullName;
        var derivedSeries = Directory.CreateDirectory(Path.Combine(root, "derived-history")).FullName;
        var sourceHead = await AssetRevisionSeries.InitializeAsync(sourceSeries, AssetManifestStore.Create(originalPath, [], DateTimeOffset.UtcNow));
        var derivedHead = await AssetRevisionSeries.InitializeAsync(derivedSeries, AssetManifestStore.Create(outputPath, [], DateTimeOffset.UtcNow));
        var sourceFile = await ProjectFileEvidence.ReadAsync(originalPath);
        var outputFile = await ProjectFileEvidence.ReadAsync(outputPath);
        var associated = new ProjectMetadataAssociation(sourceSeries, sourceHead.RevisionId, sourceHead.Sha256,
            sourceHead.RevisionId, sourceHead.Sha256, sourceHead.RevisionId, sourceHead.Sha256);
        var source = new ProjectEntry
        {
            FullPath = originalPath, ProjectId = sourceHead.ProjectId, MetadataAssociation = associated, FileIdentity = sourceFile,
        };
        source.LinkedYmmpFiles.Add(new LinkedYmmpFile { FilePath = originalPath, Role = YmmpRole.Main });
        var outputReceipt = new AssetRelinkRecovery.OutputReceipt(derivedHead, Guid.NewGuid(), originalPath, sourceFile.Sha256,
            outputPath, outputFile.Sha256, new(sourceFile.Volume, sourceFile.FileId), new(outputFile.Volume, outputFile.FileId));
        return (source, associated, new(outputReceipt, derivedSeries, sourceHead, sourceHead));
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Conflicting list association was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
