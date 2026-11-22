using System.IO;
using System.Windows.Threading;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;
using YMMProjectManager.Presentation.ViewModels;

//CSJ,class=RelinkAssociationTests,再リンク関連付けテスト
internal static class RelinkAssociationTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    internal static Task RunAsync(string workRoot) => Program.WpfTestHost.RunAsync(async () =>
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "relink-association");
        //CSJ,variable=series,系列
        var series = Path.Combine(root, "series");
        Directory.CreateDirectory(series);
        var source = Path.Combine(root, "source.ymmp");
        var output = Path.Combine(root, "output.ymmp");
        var candidate = Path.Combine(root, "new.wav");
        await File.WriteAllTextAsync(source, """{"Items":[{"FilePath":"missing.wav"}]}""");
        await File.WriteAllTextAsync(candidate, "synthetic media");
        //CSJ,variable=originalBytes,元のバイト列
        var originalBytes = await File.ReadAllBytesAsync(source);
        //CSJ,variable=projectId,プロジェクトID
        var projectId = Guid.NewGuid();
        //CSJ,variable=initial,初期
        var initial = AssetManifestStore.Create(source, [], DateTimeOffset.UtcNow, projectId: projectId);
        var head = await AssetRevisionSeries.InitializeAsync(series, initial);
        //CSJ,variable=association,関連付け
        var association = new ProjectMetadataAssociation(series, head.RevisionId, head.Sha256,
            head.RevisionId, head.Sha256, head.RevisionId, head.Sha256);

        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "test.log"));
        //CSJ,variable=list,一覧
        var list = Path.Combine(root, "projects.json");
        var failAssociationSave = false;
        //CSJ,variable=repo,リポジトリ
        var repo = new JsonProjectRepository(logger, list, beforeReplace: () =>
        {
            if (failAssociationSave) throw new IOException("synthetic association save failure");
        });
        //CSJ,variable=initialStore,初期保管先
        var initialStore = await repo.LoadAsync();
        var entry = new ProjectEntry { FullPath = source, ProjectId = projectId,
            FileIdentity = await ProjectFileEvidence.ReadAsync(source), MetadataAssociation = association };
        entry.LinkedYmmpFiles.Add(new LinkedYmmpFile { FilePath = source, Role = YmmpRole.Main });
        initialStore.Projects.Add(entry);
        await repo.SaveAsync(initialStore);
        //CSJ,variable=view,ビュー
        var view = new ProjectListViewModel(logger, repo);
        await view.InitializeAsync();
        var registered = view.Projects.Single();
        //CSJ,variable=dispatcher,ディスパッチャー
        var dispatcher = Dispatcher.CurrentDispatcher;
        var changedOnDispatcher = false;
        registered.LinkedYmmpFiles.CollectionChanged += (_, _) =>
        {
            if (!dispatcher.CheckAccess()) throw new Exception("Relink amended a bound collection off the WPF thread.");
            changedOnDispatcher = true;
        };

        var context = new RelinkDocumentContext
        {
            YmmpPath = source, OriginalBytes = originalBytes,
            Rows = [new RelinkRow { OriginalPath = "missing.wav", SelectedCandidate = candidate, Status = RelinkStatus.Updated }]
        };
        //CSJ,variable=review,レビュー
        var review = await AssetRelinkRecovery.ReviewAsync(context, series, head, head, default);
        //CSJ,variable=evidence,証跡
        var evidence = await AssetRelinkRecovery.PrepareAsync(context, output, series, head, head, review, true, default);
        if (!(await new RelinkSaveService(logger).SaveAsAsync(context, output, default)).Success)
            throw new Exception("Relink SaveAs failed.");
        //CSJ,variable=receipt,照合記録
        var receipt = await AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, head, default, source);
        failAssociationSave = true;
        try
        {
            await view.ApplyRelinkOutputAsync(registered, association, receipt, default);
            throw new Exception("Association write failure did not propagate.");
        }
        catch (IOException ex) when (ex.Message == "synthetic association save failure") { }
        if (!view.HasUnsavedProjectList || !changedOnDispatcher || (await repo.LoadAsync()).Projects.Single().FullPath != source)
            throw new Exception("Association failure silently committed or discarded the pending change.");
        var again = await AssetRelinkRecovery.CompleteForTransferAsync(evidence, series, head, default, source);
        if (again != receipt || (await AssetRevisionSeries.InspectGraphAsync(series)).Revisions.Count != 2)
            throw new Exception("Recovery duplicated the revision.");
        failAssociationSave = false;
        await view.RetryProjectListSaveAsync();
        //CSJ,variable=saved,保存済み
        var saved = (await repo.LoadAsync()).Projects.Single();
        if (view.HasUnsavedProjectList || saved.ProjectId != projectId || saved.FullPath != output ||
            saved.MetadataAssociation?.SelectedRevisionId != receipt.Revision.RevisionId ||
            saved.LinkedYmmpFiles.Single(link => link.FilePath == source).Role != YmmpRole.Backup ||
            !(await File.ReadAllBytesAsync(source)).SequenceEqual(originalBytes))
            throw new Exception("Association retry lost source bytes, identity, or revision.");
        var expected = saved.MetadataAssociation!;
        var otherSeries = Path.Combine(root, "other-series");
        Directory.CreateDirectory(otherSeries);
        await AssetRevisionSeries.InitializeAsync(otherSeries, AssetManifestStore.Create(output, [], DateTimeOffset.UtcNow, projectId: projectId));
        try
        {
            await view.ApplyMetadataDirectoryAsync(registered, expected, otherSeries, default);
            throw new Exception("A different series with the same project ID was accepted as moved.");
        }
        catch (InvalidDataException) { }
        var movedSeries = Path.Combine(root, "moved-series");
        Directory.Move(series, movedSeries);
        await view.ApplyMetadataDirectoryAsync(registered, expected, movedSeries, default);
        var rebound = (await repo.LoadAsync()).Projects.Single().MetadataAssociation;
        if (rebound != (expected with { Directory = movedSeries }))
            throw new Exception("Rebinding a moved series altered its identity or selected revision.");
        return true;
    });
}
