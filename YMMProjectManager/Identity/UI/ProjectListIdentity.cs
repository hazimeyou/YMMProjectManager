using System.IO;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,property=SelectMetadataSeriesCommand,メタデータ系列の選択コマンド
    public System.Windows.Input.ICommand SelectMetadataSeriesCommand { get; }
    //CSJ,property=ImportMetadataSeriesCommand,取り込みメタデータ系列コマンド
    public System.Windows.Input.ICommand ImportMetadataSeriesCommand { get; }
    //CSJ,property=InspectMetadataSeriesCommand,点検メタデータ系列コマンド
    public System.Windows.Input.ICommand InspectMetadataSeriesCommand { get; }
    //CSJ,property=RebindMetadataDirectoryCommand,メタデータディレクトリの再関連付けコマンド
    public System.Windows.Input.ICommand RebindMetadataDirectoryCommand { get; }
    //CSJ,property=ReassociateProjectCommand,プロジェクトの再関連付けコマンド
    public System.Windows.Input.ICommand ReassociateProjectCommand { get; }
    //CSJ,property=VerifyProjectIdentityCommand,検証プロジェクト識別情報コマンド
    public System.Windows.Input.ICommand VerifyProjectIdentityCommand { get; }
    //CSJ,property=InspectExternalAssetCommand,点検外部素材コマンド
    public System.Windows.Input.ICommand InspectExternalAssetCommand { get; }
    //CSJ,property=VerifyStoredExternalAssetsCommand,保存済みの外部素材を検証するコマンド
    public System.Windows.Input.ICommand VerifyStoredExternalAssetsCommand { get; }
    //CSJ,field=assetStorageName,素材保管名前
    private string assetStorageName = string.Empty;
    //CSJ,field=assetStoragePath,素材保管パス
    private string assetStoragePath = string.Empty;
    //CSJ,property=AssetStorageName,素材保管名前
    public string AssetStorageName { get => assetStorageName; set { if (SetProperty(ref assetStorageName, value)) inspectedAssets = null; } }
    //CSJ,property=AssetStoragePath,素材保管パス
    public string AssetStoragePath { get => assetStoragePath; set { if (SetProperty(ref assetStoragePath, value)) inspectedAssets = null; } }
    private string? inspectedExternalRoot;

    //CSJ,method=InspectExternalAssetAsync,外部素材を点検する（非同期）
    private Task InspectExternalAssetAsync() => ExecuteToolAsync("Asset external verification", async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project is null) return;
        //CSJ,variable=path,パス
        var path = AssetPath.Trim();
        //CSJ,variable=storage,保管
        var storage = new ExternalAssetLocation(AssetStorageName.Trim(), AssetStoragePath.Trim());
        var url = AssetUrl.Trim();
        if (path.Contains('\n') || string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("素材の相対パスを1件入力してください。");
        //CSJ,variable=folder,フォルダー
        var folder = new OpenFolderDialog { Title = "この操作で照合する外部保管先のルートを指定" };
        if (folder.ShowDialog() != true) return;
        inspectedAssets = null;
        inspectedExternalRoot = null;
        //CSJ,variable=asset,素材
        var asset = await AssetInventory.InspectExternalAsync(folder.FolderName, path, storage, url.Length == 0 ? null : url, token);
        if (!ReferenceEquals(project, SelectedProject) || AssetPath.Trim() != path || AssetUrl.Trim() != url ||
            AssetStorageName.Trim() != storage.StorageName || AssetStoragePath.Trim() != storage.RelativePath) return;
        inspectedAssets = [asset];
        inspectedExternalRoot = folder.FolderName;
        AssetStatus = $"外部保管先 {storage.StorageName} / {storage.RelativePath}\n{asset.Size} bytes / SHA-256 {asset.Sha256}";
    });

    private static Task<AssetVerification> VerifyInspectedAssetAsync(string projectRoot, string? externalRoot, AssetMetadata asset, CancellationToken token) =>
        externalRoot is not null && asset.ExternalLocation is not null
            ? AssetInventory.VerifyExternalAsync(externalRoot, asset, token)
            : AssetInventory.VerifyAsync(projectRoot, asset, token);

    private Task VerifyStoredExternalAssetsAsync() => ExecuteToolAsync("Asset stored external locations", async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project?.ProjectId is not Guid id || project.MetadataAssociation is not { } association) return;
        //CSJ,variable=storageName,保管名前
        var storageName = AssetStorageName.Trim();
        //CSJ,variable=revision,版
        var revision = await ResolveMetadataBranchAsync(id, association, token);
        var locations = revision.Selected.Manifest.Assets
            .Where(asset => string.Equals(asset.ExternalLocation?.StorageName, storageName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (locations.Length == 0)
        {
            //CSJ,variable=available,利用可能
            var available = revision.Selected.Manifest.Assets.Select(asset => asset.ExternalLocation?.StorageName)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            AssetStatus = available.Length == 0 ? "選択した版に外部保管先の記録がありません。" :
                $"照合する保管先名を入力してください。\n{string.Join("\n", available)}";
            return;
        }
        var picker = new OpenFolderDialog { Title = $"保管先「{storageName}」のフォルダーを指定" };
        if (picker.ShowDialog() != true) return;
        var statuses = await AssetInventory.VerifyExternalBatchAsync(picker.FolderName, locations, token);
        //CSJ,variable=results,結果一覧
        var results = locations.Zip(statuses, (asset, status) =>
        {
            //CSJ,variable=text,テキスト
            var text = status switch
            {
                AssetVerification.Valid => "一致",
                AssetVerification.Missing => "見つかりません",
                AssetVerification.SizeMismatch => "サイズが異なります",
                AssetVerification.HashMismatch => "内容が異なります",
                AssetVerification.Unreadable => "読み取れません",
                _ => "確認できません",
            };
            return $"{asset.ExternalLocation!.RelativePath}  {text}";
        });
        if (ReferenceEquals(project, SelectedProject) && project.MetadataAssociation == association &&
            string.Equals(storageName, AssetStorageName.Trim(), StringComparison.OrdinalIgnoreCase))
            AssetStatus = $"{storageName} の照合結果\n{string.Join("\n", results)}";
    });

    //CSJ,method=VerifyProjectIdentityAsync,プロジェクト識別情報を検証する（非同期）
    private Task VerifyProjectIdentityAsync() => ExecuteToolAsync("Asset project identity", async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project?.ProjectId is null || HasUnsavedProjectList || !isProjectListLossless || retainedRecoverySnapshot is not null) return;
        //CSJ,variable=path,パス
        var path = project.FullPath;
        var observed = await ProjectFileEvidence.ReadAsync(path, token);
        //CSJ,variable=known,既知の
        var known = project.FileIdentity;
        if (known == observed)
        {
            AssetStatus = "登録済みのファイル識別情報と SHA-256 が一致します。";
            return;
        }
        //CSJ,variable=reason,理由
        var reason = known is null ? "ファイル識別情報が未登録です。" :
            known.IsSameFile(observed) ? "同じファイル識別子ですが、内容が変わりました。" :
            "登録時とファイル識別子が異なります。同じパスへの置換、保存方式による置き換えなどの可能性があります。";
        if (MessageBox.Show($"{reason}\n{path}\n記録済み SHA-256 {known?.Sha256 ?? "未登録"}\n現在 SHA-256 {observed.Sha256}\n\n同じ論理プロジェクトとして現在のファイルを確認しますか？",
            "プロジェクト識別情報", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (observed != await ProjectFileEvidence.ReadAsync(path, token) || !ReferenceEquals(project, SelectedProject) ||
            project.FullPath != path || project.FileIdentity != known || HasUnsavedProjectList ||
            Projects.Any(other => !ReferenceEquals(other, project) && other.FileIdentity is { } file && file.IsSameFile(observed)))
            throw new InvalidDataException("確認中に対象または一覧が変更されました。");
        token.ThrowIfCancellationRequested();
        project.FileIdentity = observed;
        await SaveAsync();
        AssetStatus = "現在のファイル識別情報を記録しました。";
    });

    //CSJ,method=ImportMetadataSeriesAsync,メタデータ系列を取り込む（非同期）
    private Task ImportMetadataSeriesAsync() => ExecuteToolAsync("Asset legacy revision import", async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (!isInitialized || project?.ProjectId is not Guid id || HasUnsavedProjectList) return;
        //CSJ,variable=files,ファイル一覧
        var files = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Multiselect = true, CheckFileExists = true,
            Title = "取り込むメタデータ履歴のすべての版を選択" };
        if (files.ShowDialog() != true) return;
        var parent = new OpenFolderDialog { Title = "新しい履歴フォルダーを作成する場所を選択" };
        if (parent.ShowDialog() != true) return;
        //CSJ,variable=projectPath,プロジェクトのパス
        var projectPath = project.FullPath;
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.Combine(parent.FolderName, $"{Path.GetFileNameWithoutExtension(projectPath)}-asset-history");
        if (MessageBox.Show($"既存ファイルを保持したまま、履歴を次の場所に複製します。\n{directory}",
            "メタデータの取り込み", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(project, SelectedProject) || project.ProjectId != id || project.FullPath != projectPath)
            throw new InvalidDataException("取り込み対象のプロジェクトが変更されました。");
        AssetRevisionSeries.GraphInspection imported;
        try { imported = await AssetRevisionSeries.ImportLegacyGraphAsync(directory, id, files.FileNames, token); }
        catch (Exception ex) when (ex.Data[AssetRevisionSeries.RetainedImportPath] is string)
        {
            logger.Error(ex, "Legacy metadata import retained its staging directory.");
            AssetStatus = $"履歴の移行を完了できませんでした。元の履歴と作業フォルダーを保全してください。\n{ex.Data[AssetRevisionSeries.RetainedImportPath]}";
            return;
        }
        AssetStatus = $"履歴 {imported.Revisions.Count} 版、先端 {imported.Heads.Count} 件を取り込みました。関連付けは「履歴の版を選択」から行えます。\n{directory}";
    });

    //CSJ,method=SelectMetadataSeriesAsync,メタデータ系列を選択する（非同期）
    private Task SelectMetadataSeriesAsync() => ExecuteToolAsync("Asset revision selection", async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project?.ProjectId is not Guid projectId || HasUnsavedProjectList || !isProjectListLossless) return;
        //CSJ,variable=previous,直前の
        var previous = project.MetadataAssociation;
        var picker = new OpenFolderDialog { Title = "メタデータ履歴フォルダーを選択" };
        if (picker.ShowDialog() != true) return;
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetFullPath(picker.FolderName);
        //CSJ,variable=inspection,点検
        var inspection = await AssetRevisionSeries.InspectGraphAsync(directory, token);
        if (inspection.Root.ProjectId != projectId) throw new InvalidDataException("別のプロジェクト ID の履歴です。");
        if (previous is not null && (inspection.Root.RevisionId != previous.RootRevisionId || inspection.Root.Sha256 != previous.RootSha256) &&
            MessageBox.Show($"同じプロジェクト ID に別の履歴があります。\n現在のルート {previous.RootRevisionId}\n選択したルート {inspection.Root.RevisionId}\n\n履歴ファイルを保持したまま関連付けを変更しますか？",
                "メタデータ履歴の切り替え", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        //CSJ,variable=list,一覧
        var list = new System.Windows.Controls.ListBox { Margin = new Thickness(8) };
        foreach (var item in inspection.Revisions.Reverse())
            list.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = $"{(inspection.Heads.Contains(item.Receipt) ? "分岐の先端  " : "")}{item.Receipt.RevisionId}  親 {item.Manifest.ParentRevisionId}  素材 {item.Manifest.Assets.Count} 件",
                Tag = item.Receipt,
            });
        //CSJ,variable=panel,パネル
        var panel = new System.Windows.Controls.DockPanel();
        //CSJ,variable=confirm,確認
        var confirm = new System.Windows.Controls.Button { Content = "選択した版を関連付け", Margin = new Thickness(8), IsDefault = true };
        System.Windows.Controls.DockPanel.SetDock(confirm, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(confirm);
        panel.Children.Add(list);
        //CSJ,variable=window,ウィンドウ
        var window = new Window { Title = $"メタデータの版を選択（先端 {inspection.Heads.Count} 件）", Width = 960, Height = 360, Content = panel, Owner = GetActiveWindow() };
        confirm.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        if (window.ShowDialog() != true || list.SelectedItem is not System.Windows.Controls.ListBoxItem { Tag: AssetRevisionSeries.Head revision }) return;

        var head = inspection.Heads.Count == 1 ? inspection.Heads.Single() : revision;
        if (!inspection.Heads.Contains(head)) throw new InvalidDataException("履歴が分岐しています。使用する分岐の先端を選択してください。");
        var candidate = new ProjectMetadataAssociation(directory, inspection.Root.RevisionId, inspection.Root.Sha256,
            revision.RevisionId, revision.Sha256, head.RevisionId, head.Sha256);
        //CSJ,variable=resolved,解決済み
        var resolved = await ResolveMetadataBranchAsync(projectId, candidate, token);
        if (!resolved.Graph.Heads.ToHashSet().SetEquals(inspection.Heads))
            throw new InvalidDataException("確認中にメタデータ履歴の先端が変更されました。");
        if (!ReferenceEquals(project, SelectedProject) || project.ProjectId != projectId || project.MetadataAssociation != previous || HasUnsavedProjectList)
            throw new InvalidDataException("選択中のプロジェクトが変更されました。");
        token.ThrowIfCancellationRequested();
        project.MetadataAssociation = candidate;
        await SaveAsync();
        AssetStatus = "メタデータの関連付けを保存しました。";
    });

    private Task RebindMetadataDirectoryAsync() => ExecuteToolAsync("Asset series relocation", async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project?.ProjectId is not Guid id || project.MetadataAssociation is not { } association || HasUnsavedProjectList) return;
        var picker = new OpenFolderDialog { Title = "同じメタデータ履歴の移動先を指定" };
        if (picker.ShowDialog() != true) return;
        var candidate = association with { Directory = Path.GetFullPath(picker.FolderName) };
        await ResolveMetadataBranchAsync(id, candidate, token);
        if (MessageBox.Show($"移動先\n{candidate.Directory}\n確認した選択版 {candidate.SelectedRevisionId}\n\n選択版を維持して場所だけ更新しますか？",
            "メタデータ履歴の移動", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(project, SelectedProject)) return;
        await ApplyMetadataDirectoryAsync(project, association, candidate.Directory, token);
    });

    //CSJ,method=ApplyMetadataDirectoryAsync,メタデータディレクトリを適用する（非同期）
    internal async Task ApplyMetadataDirectoryAsync(ProjectEntry project, ProjectMetadataAssociation expected,
        //CSJ,parameter=directory,ディレクトリ
        string directory, CancellationToken token)
    {
        if (project.ProjectId is not Guid id) throw new InvalidDataException("プロジェクト ID を確認できません。");
        var candidate = expected with { Directory = Path.GetFullPath(directory) };
        await ResolveMetadataBranchAsync(id, candidate, token);
        if (!Projects.Contains(project) || project.ProjectId != id || project.MetadataAssociation != expected ||
            HasUnsavedProjectList || !isProjectListLossless || retainedRecoverySnapshot is not null)
            throw new InvalidDataException("対象のプロジェクトまたは関連付けが変わりました。");
        token.ThrowIfCancellationRequested();
        project.MetadataAssociation = candidate;
        await SaveAsync();
        AssetStatus = $"メタデータの場所を更新しました。\n{candidate.Directory}";
    }

    //CSJ,method=InspectMetadataSeriesAsync,メタデータ系列を点検する（非同期）
    private Task InspectMetadataSeriesAsync() => ExecuteToolAsync("Asset revision verification", async token =>
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project?.ProjectId is not Guid id || project.MetadataAssociation is not { } association) return;
        //CSJ,variable=selection,選択
        var selection = await ResolveMetadataBranchAsync(id, association, token);
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(Path.GetFullPath(project.FullPath))!;
        //CSJ,variable=lines,行一覧
        var lines = new List<string> { $"選択版 {selection.Selected.Receipt.RevisionId}", $"先端 {selection.Graph.Heads.Count} 件" };
        //CSJ,variable=root,ルート
        var root = selection.Graph.Revisions.Single(revision => revision.Receipt == selection.Graph.Root);
        if (root.Manifest.DerivedFrom is { } origin)
            lines.Add($"派生元プロジェクト {origin.SourceProjectId}\n派生元の版 {origin.SourceRevisionId}\n版のSHA-256 {origin.SourceRevisionSha256}");
        lines.AddRange(selection.Graph.Heads.Select(head => head.RevisionId.ToString()));
        if (!selection.Graph.Heads.Any(head => head.RevisionId == association.ObservedHeadRevisionId && head.Sha256 == association.ObservedHeadSha256))
            lines.Add("関連付け時に選んだ分岐の先端が変わりました。");
        //CSJ,variable=asset,素材
        foreach (var asset in selection.Selected.Manifest.Assets)
        {
            token.ThrowIfCancellationRequested();
            if (asset.ExternalRelink is { } external)
                lines.Add($"{external.OriginalFileName} / SHA-256 {asset.Sha256} / 外部素材。許可したフォルダーで再照合してください。");
            else
                lines.Add($"{asset.RelativePath}: {await AssetInventory.VerifyAsync(directory, asset, token)}");
        }
        if (ReferenceEquals(project, SelectedProject)) AssetStatus = string.Join("\n", lines);
    });

    //CSJ,method=ResolveMetadataBranchAsync,メタデータブランチを解決する（非同期）
    private static Task<AssetRevisionSeries.BranchSelection> ResolveMetadataBranchAsync(Guid id, ProjectMetadataAssociation association, CancellationToken token) =>
        AssetRevisionSeries.ResolveBranchAsync(association.Directory, id, association.RootRevisionId, association.RootSha256,
            association.SelectedRevisionId, association.SelectedSha256, token);

    //CSJ,method=ApplyRelinkOutputAsync,再リンク出力を適用する（非同期）
    internal async Task ApplyRelinkOutputAsync(ProjectEntry project, ProjectMetadataAssociation association,
        //CSJ,parameter=receipt,照合記録
        AssetRelinkRecovery.OutputReceipt receipt, CancellationToken token)
    {
        //CSJ,variable=revision,版
        var revision = receipt.Revision;
        //CSJ,variable=replacement,置換
        var replacement = association with { SelectedRevisionId = revision.RevisionId, SelectedSha256 = revision.Sha256,
            ObservedHeadRevisionId = revision.RevisionId, ObservedHeadSha256 = revision.Sha256 };
        //CSJ,variable=dispatcher,ディスパッチャー
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await AssetRelinkRecovery.WithVerifiedProjectsAsync(receipt, heldToken =>
            dispatcher.InvokeAsync(() => PersistOnUiAsync(heldToken)).Task.Unwrap(), token);
        PrepareThumbnailMetadata([project]);

        async Task PersistOnUiAsync(CancellationToken heldToken)
        {
            var selected = await ResolveMetadataBranchAsync(revision.ProjectId, replacement, heldToken);
            //CSJ,variable=fileIdentity,ファイル識別情報
            var fileIdentity = await ProjectFileEvidence.ReadAsync(receipt.Output, heldToken);
            //CSJ,variable=sourceIdentity,移行元の識別情報
            var sourceIdentity = await ProjectFileEvidence.ReadAsync(receipt.Source, heldToken);
            if (!selected.Graph.Heads.Contains(revision) || project.ProjectId != revision.ProjectId || project.MetadataAssociation != association ||
                !Projects.Contains(project) || !isProjectListLossless || retainedRecoverySnapshot is not null || HasUnsavedProjectList ||
                (project.FileIdentity is { } knownSource && !knownSource.IsSameFile(sourceIdentity)) ||
                !string.Equals(project.FullPath, receipt.Source, StringComparison.OrdinalIgnoreCase) ||
                Projects.Any(entry => !ReferenceEquals(entry, project) && string.Equals(Path.GetFullPath(entry.FullPath), receipt.Output, StringComparison.OrdinalIgnoreCase)) ||
                project.LinkedYmmpFiles.Any(link => string.Equals(link.FilePath, receipt.Output, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("一覧、移行先、またはメタデータの先端が変わりました。");
            heldToken.ThrowIfCancellationRequested();
            project.FullPath = receipt.Output;
            project.MetadataAssociation = replacement;
            project.FileIdentity = fileIdentity;
            //CSJ,variable=link,リンク
            foreach (var link in project.LinkedYmmpFiles.Where(link => link.Role == YmmpRole.Main && string.Equals(link.FilePath, receipt.Source, StringComparison.OrdinalIgnoreCase)))
                link.Role = YmmpRole.Backup;
            project.LinkedYmmpFiles.Add(new LinkedYmmpFile { FilePath = receipt.Output, Role = YmmpRole.Main, Exists = true, LastCheckedAt = DateTimeOffset.Now });
            pendingAdditionEvidence[receipt.Output] = (revision.ProjectId, receipt.OutputSha256);
            await SaveAsync();
            if (HasUnsavedProjectList) throw new IOException("一覧に追加の未保存変更が残っています。");
        }
    }

    private async Task ReassociateProjectAsync()
    {
        //CSJ,variable=project,プロジェクト
        var project = SelectedProject;
        if (project?.ProjectId is null || HasUnsavedProjectList) return;
        if (!IsMissingProjectFile(project.FullPath))
        {
            AssetStatus = "元のファイルが存在するか、状態を確認できません。独立したコピーは「追加」で登録してください。";
            return;
        }
        await ExecuteToolAsync("Asset project relocation", async token =>
        {
            var picker = new OpenFileDialog { Filter = "YMM Project (*.ymmp)|*.ymmp", CheckFileExists = true };
            if (picker.ShowDialog() != true) return;
            //CSJ,variable=oldPath,旧パス
            var oldPath = project.FullPath;
            //CSJ,variable=newPath,新規パス
            var newPath = Path.GetFullPath(picker.FileName);
            var confirmedIdentity = await ProjectFileEvidence.ReadAsync(newPath, token);
            if (MessageBox.Show($"元の登録先\n{oldPath}\n\n移動・改名先\n{newPath}\nSHA-256 {confirmedIdentity.Sha256}\n\n同じ論理プロジェクトとして既存 ID を維持しますか？", "プロジェクトの移動・改名", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            token.ThrowIfCancellationRequested();
            if (project.FullPath != oldPath || !ReferenceEquals(project, SelectedProject))
                throw new InvalidDataException("確認中に対象が変更されました。");
            await ApplyProjectRelocationAsync(project, newPath, confirmedIdentity, token);
        });
    }

    internal async Task ApplyProjectRelocationAsync(ProjectEntry project, string newPath, ProjectFileIdentity? confirmed = null, CancellationToken token = default)
    {
        newPath = Path.GetFullPath(newPath);
        //CSJ,variable=oldPath,旧パス
        var oldPath = project.FullPath;
        if (project.ProjectId is null || !isProjectListLossless || HasUnsavedProjectList || !Projects.Contains(project) ||
            !IsMissingProjectFile(oldPath) || !File.Exists(newPath) || !newPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            Projects.Any(p => !ReferenceEquals(p, project) && string.Equals(Path.GetFullPath(p.FullPath), newPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("移動前後の状態が変わったか、移動先が既に登録されています。");

        using var destinationDirectory = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(newPath)!, enforceShare: true);
        //CSJ,variable=destination,格納先
        await using var destination = ProjectFileEvidence.OpenRead(newPath);
        //CSJ,variable=identity,識別情報
        var identity = await ProjectFileEvidence.ReadAsync(destination, token);
        if ((confirmed is not null && identity != confirmed) || !IsMissingProjectFile(oldPath) || project.FullPath != oldPath || HasUnsavedProjectList ||
            Projects.Any(other => !ReferenceEquals(other, project) && other.FileIdentity is { } file && file.IsSameFile(identity)))
            throw new InvalidDataException("確認中に移動元または一覧が変更されました。");
        token.ThrowIfCancellationRequested();
        project.FullPath = newPath;
        project.FileIdentity = identity;
        pendingAdditionEvidence[newPath] = (project.ProjectId!.Value, identity.Sha256);
        //CSJ,variable=link,リンク
        foreach (var link in project.LinkedYmmpFiles.Where(l => string.Equals(l.FilePath, oldPath, StringComparison.OrdinalIgnoreCase)))
        {
            link.FilePath = newPath;
            link.Exists = true;
            link.LastCheckedAt = DateTimeOffset.Now;
        }
        await SaveAsync();
        SelectedProject = null;
        SelectedProject = project;
        PrepareThumbnailMetadata([project]);
        AssetStatus = "移動・改名先を登録しました。素材の相対パスは必要に応じて照合してください。";
    }

    //CSJ,method=IsMissingProjectFile,欠落プロジェクトファイルかを判定する
    private static bool IsMissingProjectFile(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
