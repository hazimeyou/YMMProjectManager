using System.IO;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public System.Windows.Input.ICommand SelectMetadataSeriesCommand { get; }
    public System.Windows.Input.ICommand ImportMetadataSeriesCommand { get; }
    public System.Windows.Input.ICommand InspectMetadataSeriesCommand { get; }
    public System.Windows.Input.ICommand RebindMetadataDirectoryCommand { get; }
    public System.Windows.Input.ICommand ReassociateProjectCommand { get; }
    public System.Windows.Input.ICommand VerifyProjectIdentityCommand { get; }
    public System.Windows.Input.ICommand InspectExternalAssetCommand { get; }
    public System.Windows.Input.ICommand VerifyStoredExternalAssetsCommand { get; }
    private string assetStorageName = string.Empty;
    private string assetStoragePath = string.Empty;
    public string AssetStorageName { get => assetStorageName; set { if (SetProperty(ref assetStorageName, value)) inspectedAssets = null; } }
    public string AssetStoragePath { get => assetStoragePath; set { if (SetProperty(ref assetStoragePath, value)) inspectedAssets = null; } }
    private string? inspectedExternalRoot;

    private Task InspectExternalAssetAsync() => ExecuteToolAsync("Asset external verification", async token =>
    {
        var project = SelectedProject;
        if (project is null) return;
        var path = AssetPath.Trim();
        var storage = new ExternalAssetLocation(AssetStorageName.Trim(), AssetStoragePath.Trim());
        var url = AssetUrl.Trim();
        if (path.Contains('\n') || string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("素材の相対パスを1件入力してください。");
        var folder = new OpenFolderDialog { Title = "この操作で照合する外部保管先のルートを指定" };
        if (folder.ShowDialog() != true) return;
        inspectedAssets = null;
        inspectedExternalRoot = null;
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
        var project = SelectedProject;
        if (project?.ProjectId is not Guid id || project.MetadataAssociation is not { } association) return;
        var storageName = AssetStorageName.Trim();
        var revision = await ResolveMetadataBranchAsync(id, association, token);
        var locations = revision.Selected.Manifest.Assets
            .Where(asset => string.Equals(asset.ExternalLocation?.StorageName, storageName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (locations.Length == 0)
        {
            var available = revision.Selected.Manifest.Assets.Select(asset => asset.ExternalLocation?.StorageName)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            AssetStatus = available.Length == 0 ? "選択した版に外部保管先の記録がありません。" :
                $"照合する保管先名を入力してください。\n{string.Join("\n", available)}";
            return;
        }
        var picker = new OpenFolderDialog { Title = $"保管先「{storageName}」のフォルダーを指定" };
        if (picker.ShowDialog() != true) return;
        var results = new List<string>(locations.Length);
        foreach (var asset in locations)
        {
            token.ThrowIfCancellationRequested();
            var status = await AssetInventory.VerifyExternalAsync(picker.FolderName, asset, token);
            var text = status switch
            {
                AssetVerification.Valid => "一致",
                AssetVerification.Missing => "見つかりません",
                AssetVerification.SizeMismatch => "サイズが異なります",
                AssetVerification.HashMismatch => "内容が異なります",
                _ => "確認できません",
            };
            results.Add($"{asset.ExternalLocation!.RelativePath}  {text}");
        }
        if (ReferenceEquals(project, SelectedProject) && project.MetadataAssociation == association)
            AssetStatus = $"{storageName} の照合結果\n{string.Join("\n", results)}";
    });

    private Task VerifyProjectIdentityAsync() => ExecuteToolAsync("Asset project identity", async token =>
    {
        var project = SelectedProject;
        if (project?.ProjectId is null || HasUnsavedProjectList || !isProjectListLossless || retainedRecoverySnapshot is not null) return;
        var path = project.FullPath;
        var observed = await ProjectFileEvidence.ReadAsync(path, token);
        var known = project.FileIdentity;
        if (known == observed)
        {
            AssetStatus = "登録済みのファイル識別情報と SHA-256 が一致します。";
            return;
        }
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

    private Task ImportMetadataSeriesAsync() => ExecuteToolAsync("Asset legacy revision import", async token =>
    {
        var project = SelectedProject;
        if (!isInitialized || project?.ProjectId is not Guid id || HasUnsavedProjectList) return;
        var files = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Multiselect = true, CheckFileExists = true,
            Title = "取り込むメタデータ履歴のすべての版を選択" };
        if (files.ShowDialog() != true) return;
        var parent = new OpenFolderDialog { Title = "新しい履歴フォルダーを作成する場所を選択" };
        if (parent.ShowDialog() != true) return;
        var projectPath = project.FullPath;
        var directory = Path.Combine(parent.FolderName, $"{Path.GetFileNameWithoutExtension(projectPath)}-asset-history");
        if (MessageBox.Show($"既存ファイルを保持したまま、履歴を次の場所に複製します。\n{directory}",
            "メタデータの取り込み", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(project, SelectedProject) || project.ProjectId != id || project.FullPath != projectPath)
            throw new InvalidDataException("取り込み対象のプロジェクトが変更されました。");
        var imported = await AssetRevisionSeries.ImportLegacyGraphAsync(directory, id, files.FileNames, token);
        AssetStatus = $"履歴 {imported.Revisions.Count} 版、先端 {imported.Heads.Count} 件を取り込みました。関連付けは「履歴の版を選択」から行えます。\n{directory}";
    });

    private Task SelectMetadataSeriesAsync() => ExecuteToolAsync("Asset revision selection", async token =>
    {
        var project = SelectedProject;
        if (project?.ProjectId is not Guid projectId || HasUnsavedProjectList || !isProjectListLossless) return;
        var previous = project.MetadataAssociation;
        var picker = new OpenFolderDialog { Title = "メタデータ履歴フォルダーを選択" };
        if (picker.ShowDialog() != true) return;
        var directory = Path.GetFullPath(picker.FolderName);
        var inspection = await AssetRevisionSeries.InspectGraphAsync(directory, token);
        if (inspection.Root.ProjectId != projectId) throw new InvalidDataException("別のプロジェクト ID の履歴です。");
        if (previous is not null && (inspection.Root.RevisionId != previous.RootRevisionId || inspection.Root.Sha256 != previous.RootSha256) &&
            MessageBox.Show($"同じプロジェクト ID に別の履歴があります。\n現在のルート {previous.RootRevisionId}\n選択したルート {inspection.Root.RevisionId}\n\n履歴ファイルを保持したまま関連付けを変更しますか？",
                "メタデータ履歴の切り替え", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var list = new System.Windows.Controls.ListBox { Margin = new Thickness(8) };
        foreach (var item in inspection.Revisions.Reverse())
            list.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = $"{(inspection.Heads.Contains(item.Receipt) ? "分岐の先端  " : "")}{item.Receipt.RevisionId}  親 {item.Manifest.ParentRevisionId}  素材 {item.Manifest.Assets.Count} 件",
                Tag = item.Receipt,
            });
        var panel = new System.Windows.Controls.DockPanel();
        var confirm = new System.Windows.Controls.Button { Content = "選択した版を関連付け", Margin = new Thickness(8), IsDefault = true };
        System.Windows.Controls.DockPanel.SetDock(confirm, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(confirm);
        panel.Children.Add(list);
        var window = new Window { Title = $"メタデータの版を選択（先端 {inspection.Heads.Count} 件）", Width = 960, Height = 360, Content = panel, Owner = GetActiveWindow() };
        confirm.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        if (window.ShowDialog() != true || list.SelectedItem is not System.Windows.Controls.ListBoxItem { Tag: AssetRevisionSeries.Head revision }) return;

        var head = inspection.Heads.Count == 1 ? inspection.Heads.Single() : revision;
        if (!inspection.Heads.Contains(head)) throw new InvalidDataException("履歴が分岐しています。使用する分岐の先端を選択してください。");
        var candidate = new ProjectMetadataAssociation(directory, inspection.Root.RevisionId, inspection.Root.Sha256,
            revision.RevisionId, revision.Sha256, head.RevisionId, head.Sha256);
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

    internal async Task ApplyMetadataDirectoryAsync(ProjectEntry project, ProjectMetadataAssociation expected,
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

    private Task InspectMetadataSeriesAsync() => ExecuteToolAsync("Asset revision verification", async token =>
    {
        var project = SelectedProject;
        if (project?.ProjectId is not Guid id || project.MetadataAssociation is not { } association) return;
        var selection = await ResolveMetadataBranchAsync(id, association, token);
        var directory = Path.GetDirectoryName(Path.GetFullPath(project.FullPath))!;
        var lines = new List<string> { $"選択版 {selection.Selected.Receipt.RevisionId}", $"先端 {selection.Graph.Heads.Count} 件" };
        lines.AddRange(selection.Graph.Heads.Select(head => head.RevisionId.ToString()));
        if (!selection.Graph.Heads.Any(head => head.RevisionId == association.ObservedHeadRevisionId && head.Sha256 == association.ObservedHeadSha256))
            lines.Add("関連付け時に選んだ分岐の先端が変わりました。");
        foreach (var asset in selection.Selected.Manifest.Assets)
        {
            token.ThrowIfCancellationRequested();
            lines.Add($"{asset.RelativePath}: {await AssetInventory.VerifyAsync(directory, asset, token)}");
        }
        if (ReferenceEquals(project, SelectedProject)) AssetStatus = string.Join("\n", lines);
    });

    private static Task<AssetRevisionSeries.BranchSelection> ResolveMetadataBranchAsync(Guid id, ProjectMetadataAssociation association, CancellationToken token) =>
        AssetRevisionSeries.ResolveBranchAsync(association.Directory, id, association.RootRevisionId, association.RootSha256,
            association.SelectedRevisionId, association.SelectedSha256, token);

    internal async Task ApplyRelinkOutputAsync(ProjectEntry project, ProjectMetadataAssociation association,
        AssetRelinkRecovery.OutputReceipt receipt, CancellationToken token)
    {
        var revision = receipt.Revision;
        var replacement = association with { SelectedRevisionId = revision.RevisionId, SelectedSha256 = revision.Sha256,
            ObservedHeadRevisionId = revision.RevisionId, ObservedHeadSha256 = revision.Sha256 };
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await AssetRelinkRecovery.WithVerifiedProjectsAsync(receipt, heldToken =>
            dispatcher.InvokeAsync(() => PersistOnUiAsync(heldToken)).Task.Unwrap(), token);
        PrepareThumbnailMetadata([project]);

        async Task PersistOnUiAsync(CancellationToken heldToken)
        {
            var selected = await ResolveMetadataBranchAsync(revision.ProjectId, replacement, heldToken);
            var fileIdentity = await ProjectFileEvidence.ReadAsync(receipt.Output, heldToken);
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
            var oldPath = project.FullPath;
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
        var oldPath = project.FullPath;
        if (project.ProjectId is null || !isProjectListLossless || HasUnsavedProjectList || !Projects.Contains(project) ||
            !IsMissingProjectFile(oldPath) || !File.Exists(newPath) || !newPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            Projects.Any(p => !ReferenceEquals(p, project) && string.Equals(Path.GetFullPath(p.FullPath), newPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("移動前後の状態が変わったか、移動先が既に登録されています。");

        await using var destination = ProjectFileEvidence.OpenRead(newPath);
        var identity = await ProjectFileEvidence.ReadAsync(destination, token);
        if ((confirmed is not null && identity != confirmed) || !IsMissingProjectFile(oldPath) || project.FullPath != oldPath || HasUnsavedProjectList ||
            Projects.Any(other => !ReferenceEquals(other, project) && other.FileIdentity is { } file && file.IsSameFile(identity)))
            throw new InvalidDataException("確認中に移動元または一覧が変更されました。");
        token.ThrowIfCancellationRequested();
        project.FullPath = newPath;
        project.FileIdentity = identity;
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

    private static bool IsMissingProjectFile(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
