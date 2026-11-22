using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,record=GitChange,Git変更
    public sealed record GitChange(string Code, string Path, string? OriginalPath)
    {
        //CSJ,property=Display,表示
        public string Display => OriginalPath is null ? $"{Code}  {Path}" : $"{Code}  {OriginalPath} → {Path}";
    }

    //CSJ,property=GitChanges,Git変更一覧
    public ObservableCollection<GitChange> GitChanges { get; } = [];

    //CSJ,property=SelectedGitChange,選択中のGit変更
    public GitChange? SelectedGitChange
    {
        get => selectedGitChange;
        set
        {
            if (SetProperty(ref selectedGitChange, value) && value is not null) GitPath = value.Path;
        }
    }
    //CSJ,field=selectedGitChange,選択中のGit変更
    private GitChange? selectedGitChange;

    //CSJ,method=UpdateGitChanges,Git変更一覧を更新する
    private void UpdateGitChanges(string porcelain)
    {
        //CSJ,variable=entries,項目一覧
        var entries = porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        GitChanges.Clear();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4 || entry[2] != ' ') continue;
            //CSJ,variable=code,コード
            var code = entry[..2];
            var origin = (code.Contains('R') || code.Contains('C')) && i + 1 < entries.Length ? entries[++i] : null;
            GitChanges.Add(new GitChange(code, entry[3..], origin));
        }
    }

    //CSJ,method=ConfirmGitCommit,Gitコミットを確認する
    private static bool ConfirmGitCommit(GitCommitTarget target, string message)
    {
        //CSJ,variable=window,ウィンドウ
        var window = new Window
        {
            Title = "ステージ済みの変更をコミット",
            Width = 680, Height = 500, MinWidth = 360, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var owner = GetActiveWindow();
        if (owner is not null) window.Owner = owner;
        //CSJ,variable=panel,パネル
        var panel = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = "コミット", MinWidth = 100, Margin = new Thickness(0, 8, 8, 0) };
        accept.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = "キャンセル", MinWidth = 100, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) });
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBox
        {
            Text = $"{target.RepositoryRoot}\n{target.BranchRef}\n親 {target.ParentObjectId ?? "なし"}\n\n{message}\n\nステージ済み {target.Paths.Count} 件\n" +
                string.Join("\n", target.Paths),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        window.Content = panel;
        return window.ShowDialog() == true;
    }

    //CSJ,method=DescribeCommitFailure,コミット失敗を説明する
    private static string DescribeCommitFailure(GitResult result) => result.Failure switch
    {
        GitFailure.MediaPublication => "素材、パッケージ、ローカル専用の回復記録が含まれるか、内容を確認できないためGit操作を中止しました。",
        GitFailure.ConcurrentOperation => "別のGit操作で状態が変わりました。変更一覧を更新し、再確認してください。",
        GitFailure.OutcomeUnknown => "コミットの結果を確認できません。再試行前に履歴とステージ済みの状態を確認してください。",
        _ => "Git操作を完了できません。リポジトリ、Gitの利用者設定、選択したファイル、ステージ済みの状態を確認してください。",
    };

    //CSJ,field=MaxPublicationProjectBytes,最大確定反映プロジェクトバイト列
    private const int MaxPublicationProjectBytes = 64 * 1024 * 1024;

    //CSJ,method=RegisterKnownPublicationAssetsAsync,既知の確定反映素材一覧を登録する（非同期）
    private async Task RegisterKnownPublicationAssetsAsync(GitRepository git, ProjectEntry project, CancellationToken token)
    {
        // Read the selected PF itself. Embedded paths are treated as names and are never opened.
        //CSJ,variable=file,ファイル
        await using var file = ProjectFileEvidence.OpenRead(project.FullPath);
        if (file.Length > MaxPublicationProjectBytes)
            throw new InvalidDataException("素材混入の確認に対応するPFのサイズを超えています。");
        //CSJ,variable=reader,読み取り元
        using var reader = new StreamReader(file);
        //CSJ,variable=text,テキスト
        var text = await reader.ReadToEndAsync(token);
        using var parsed = System.Text.Json.JsonDocument.Parse(text);
        if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidDataException("素材混入を検査できるPFではありません。");
        var referencedNames = PackagingDetector.FindFilePaths(text);
        git.SetPublicationAssets(referencedNames, []);
        if (project.ProjectId is Guid id && project.MetadataAssociation is { } association)
        {
            //CSJ,variable=known,既知の
            var known = (await ResolveMetadataBranchAsync(id, association, token)).Selected.Manifest.Assets;
            git.SetPublicationAssets(known.Select(asset => asset.RelativePath), known.Select(asset => asset.Sha256));
        }
        git.SetPublicationAssets(collectedPoolAssets.SelectMany(asset => new[] { asset.OriginalFileName, asset.DistributionFileName }),
            collectedPoolAssets.Select(asset => asset.Sha256));
    }
}
