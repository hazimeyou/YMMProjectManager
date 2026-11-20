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
    public System.Windows.Input.ICommand ReassociateProjectCommand { get; }

    private Task ImportMetadataSeriesAsync() => ExecuteToolAsync("Asset legacy revision import", async token =>
    {
        var project = SelectedProject;
        if (!isInitialized || project?.ProjectId is not Guid id || HasUnsavedProjectList) return;
        var files = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Multiselect = true, CheckFileExists = true,
            Title = "取り込むメタデータ履歴のすべての版を選択" };
        if (files.ShowDialog() != true) return;
        var parent = new OpenFolderDialog { Title = "新しい履歴フォルダーを作成する場所を選択" };
        if (parent.ShowDialog() != true) return;
        var directory = Path.Combine(parent.FolderName, $"{Path.GetFileNameWithoutExtension(project.FullPath)}-asset-history");
        if (MessageBox.Show($"既存ファイルを保持したまま、履歴を次の場所に複製します。\n{directory}",
            "メタデータの取り込み", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var imported = await AssetRevisionSeries.ImportLegacyAsync(directory, id, files.FileNames, token);
        AssetStatus = $"履歴 {imported.Revisions.Count} 版を取り込みました。関連付けは「履歴の版を選択」から行えます。\n{directory}";
    });

    private Task SelectMetadataSeriesAsync() => ExecuteToolAsync("Asset revision selection", async token =>
    {
        var project = SelectedProject;
        if (project?.ProjectId is not Guid projectId || HasUnsavedProjectList || !isProjectListLossless) return;
        var picker = new OpenFolderDialog { Title = "メタデータ履歴フォルダーを選択" };
        if (picker.ShowDialog() != true) return;
        var directory = Path.GetFullPath(picker.FolderName);
        var inspection = await AssetRevisionSeries.InspectAsync(directory, token);
        if (inspection.Root.ProjectId != projectId) throw new InvalidDataException("別のプロジェクト ID の履歴です。");
        var list = new System.Windows.Controls.ListBox { Margin = new Thickness(8) };
        foreach (var item in inspection.Revisions.Reverse())
            list.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = $"{item.Manifest.VerifiedAtUtc:g}  {item.Receipt.RevisionId}  素材 {item.Manifest.Assets.Count} 件",
                Tag = item.Receipt,
            });
        var panel = new System.Windows.Controls.DockPanel();
        var confirm = new System.Windows.Controls.Button { Content = "選択した版を関連付け", Margin = new Thickness(8), IsDefault = true };
        System.Windows.Controls.DockPanel.SetDock(confirm, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(confirm);
        panel.Children.Add(list);
        var window = new Window { Title = "メタデータの版を選択", Width = 620, Height = 360, Content = panel, Owner = GetActiveWindow() };
        confirm.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        if (window.ShowDialog() != true || list.SelectedItem is not System.Windows.Controls.ListBoxItem { Tag: AssetRevisionSeries.Head revision }) return;

        var candidate = new ProjectMetadataAssociation(directory, inspection.Root.RevisionId, inspection.Root.Sha256,
            revision.RevisionId, revision.Sha256, inspection.Head.RevisionId, inspection.Head.Sha256);
        var resolved = await ResolveMetadataAsync(projectId, candidate, token);
        if (!ReferenceEquals(project, SelectedProject) || project.ProjectId != projectId || HasUnsavedProjectList)
            throw new InvalidDataException("選択中のプロジェクトが変更されました。");
        token.ThrowIfCancellationRequested();
        project.MetadataAssociation = candidate with { ObservedHeadRevisionId = resolved.Head.RevisionId, ObservedHeadSha256 = resolved.Head.Sha256 };
        await SaveAsync();
        AssetStatus = "メタデータの関連付けを保存しました。";
    });

    private Task InspectMetadataSeriesAsync() => ExecuteToolAsync("Asset revision verification", async token =>
    {
        var project = SelectedProject;
        if (project?.ProjectId is not Guid id || project.MetadataAssociation is not { } association) return;
        var selection = await ResolveMetadataAsync(id, association, token);
        var directory = Path.GetDirectoryName(Path.GetFullPath(project.FullPath))!;
        var lines = new List<string> { $"選択版 {selection.Selected.Receipt.RevisionId}", $"現在の head {selection.Head.RevisionId}" };
        if (selection.Head.RevisionId != association.ObservedHeadRevisionId || selection.Head.Sha256 != association.ObservedHeadSha256)
            lines.Add("関連付け保存時から head が変わりました。");
        foreach (var asset in selection.Selected.Manifest.Assets)
        {
            token.ThrowIfCancellationRequested();
            lines.Add($"{asset.RelativePath}: {await AssetInventory.VerifyAsync(directory, asset, token)}");
        }
        if (ReferenceEquals(project, SelectedProject)) AssetStatus = string.Join("\n", lines);
    });

    private static Task<AssetRevisionSeries.Selection> ResolveMetadataAsync(Guid id, ProjectMetadataAssociation association, CancellationToken token) =>
        AssetRevisionSeries.ResolveAsync(association.Directory, id, association.RootRevisionId, association.RootSha256,
            association.SelectedRevisionId, association.SelectedSha256, token);

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
            var hash = await HashRecoveryFileAsync(newPath);
            if (MessageBox.Show($"元の登録先\n{oldPath}\n\n移動・改名先\n{newPath}\nSHA-256 {hash}\n\n同じ論理プロジェクトとして既存 ID を維持しますか？", "プロジェクトの移動・改名", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            token.ThrowIfCancellationRequested();
            if (hash != await HashRecoveryFileAsync(newPath) || project.FullPath != oldPath || !ReferenceEquals(project, SelectedProject))
                throw new InvalidDataException("確認中に対象が変更されました。");
            await ApplyProjectRelocationAsync(project, newPath);
        });
    }

    internal async Task ApplyProjectRelocationAsync(ProjectEntry project, string newPath)
    {
        newPath = Path.GetFullPath(newPath);
        var oldPath = project.FullPath;
        if (project.ProjectId is null || !isProjectListLossless || HasUnsavedProjectList || !Projects.Contains(project) ||
            !IsMissingProjectFile(oldPath) || !File.Exists(newPath) || !newPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            Projects.Any(p => !ReferenceEquals(p, project) && string.Equals(Path.GetFullPath(p.FullPath), newPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("移動前後の状態が変わったか、移動先が既に登録されています。");

        project.FullPath = newPath;
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
