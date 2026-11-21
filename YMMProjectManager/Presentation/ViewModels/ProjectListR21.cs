using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand ReviewProjectCleanupCommand { get; }
    public ICommand CollectAssetToPoolCommand { get; }
    public ICommand InspectGitHubAccessCommand { get; }
    private string? lastApprovedPoolRoot;
    private readonly List<AssetPoolRecord> collectedPoolAssets = [];
    private bool useCachedGitHubAuthentication;

    public bool UseCachedGitHubAuthentication
    {
        get => useCachedGitHubAuthentication;
        set => SetProperty(ref useCachedGitHubAuthentication, value);
    }

    private GitHubAuthentication AuthenticationFor(string endpoint) =>
        UseCachedGitHubAuthentication && GitRepository.IsSupportedGitHubEndpoint(endpoint)
            ? GitHubAuthentication.CachedWindows : GitHubAuthentication.Anonymous;

    private static string AuthenticationNotice(GitHubAuthentication authentication) =>
        authentication == GitHubAuthentication.CachedWindows
            ? "\nGitHubへの接続に、Windowsに保存済みのGit Credential Manager資格情報を使用します。"
            : "";

    private Task InspectGitHubAccessAsync() => ExecuteToolAsync("GitHub access", async token =>
    {
        var project = SelectedProject;
        if (project is null) return;
        var located = await LocateProjectGitRootAsync(project, token);
        if (located is null) { GitStatus = "Gitリポジトリを確認できません。"; return; }
        var (git, root) = located.Value;
        var origin = await git.OriginAsync(token);
        var endpoint = origin.StandardOutput;
        if (!origin.Success || !GitRepository.IsSupportedGitHubEndpoint(endpoint))
        {
            GitStatus = "このリポジトリのoriginに https://github.com/所有者/リポジトリ を指定してください。";
            return;
        }
        var authentication = AuthenticationFor(endpoint);
        if (!ReferenceEquals(project, SelectedProject) ||
            !ConfirmGitTrust("GitHubのアクセスを確認", root, endpoint + AuthenticationNotice(authentication))) return;
        GitStatus = "GitHubへのアクセスを確認しています。";
        var result = await git.ProbeGitHubAccessAsync(endpoint, authentication, token);
        GitStatus = result.Success
            ? "対象リポジトリを読み取れます。公開リポジトリでは、応答だけでログイン済みかどうかを判定できません。"
            : DescribeRemoteFailure(result);
    });

    private Task ReviewProjectCleanupAsync() => ExecuteToolAsync("Asset PF cleanup", async token =>
    {
        var project = SelectedProject;
        if (project is null) return;
        var path = project.FullPath;
        var service = new ProjectFileCleanupService();
        var preview = await service.InspectAsync(path, token);
        if (!ReferenceEquals(project, SelectedProject) || project.FullPath != path) return;
        var report = preview.FormatForDisplay();
        if (!preview.CanSave)
        {
            MessageBox.Show(report, "PF 整理の確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(report + "\n\n別名のPFに保存しますか？", "PF 整理の確認", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var output = new SaveFileDialog
        {
            Title = "元のプロジェクトと同じフォルダーに別名で保存",
            Filter = "YMM Project (*.ymmp)|*.ymmp",
            InitialDirectory = Path.GetDirectoryName(path),
            FileName = Path.GetFileNameWithoutExtension(path) + "-整理済み.ymmp",
            OverwritePrompt = false,
        };
        if (output.ShowDialog() != true) return;
        await service.SaveAsAsync(preview, output.FileName, token);
        AssetStatus = $"整理したPFを保存しました。\n{output.FileName}";
    });

    private Task CollectAssetToPoolAsync() => ExecuteToolAsync("Asset pool collection", async token =>
    {
        var project = SelectedProject;
        if (project is null) return;
        var references = AssetPath.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (references.Length != 1)
        {
            AssetStatus = "収集する素材の元の参照を、素材パス欄に1件入力してください。";
            return;
        }
        var location = await LocateProjectGitRootAsync(project, token);
        if (location is null)
        {
            AssetStatus = "素材が使われるGitコミットを記録するため、このプロジェクトのGitリポジトリが必要です。";
            return;
        }
        var tip = await location.Value.Repository.InspectCurrentCommitAsync(token);
        if (!tip.Success)
        {
            AssetStatus = "現在のブランチのコミットIDを確認できません。先にプロジェクトをコミットしてください。";
            return;
        }
        var path = project.FullPath;
        var expectedName = AssetPool.GetOriginalFileName(references[0]);
        if (!await location.Value.Repository.ConfirmCommittedAssetReferenceAsync(tip.StandardOutput, path, references[0], token))
        {
            AssetStatus = "現在のコミットにこのPFと指定した素材参照がありません。履歴と素材パスを確認してください。";
            return;
        }
        var picker = new OpenFileDialog { Title = "収集する素材を選択", CheckFileExists = true, FileName = expectedName };
        if (!ReferenceEquals(project, SelectedProject) || picker.ShowDialog() != true) return;
        var pool = new OpenFolderDialog { Title = "素材プールの保存先を選択", InitialDirectory = lastApprovedPoolRoot ?? "" };
        if (pool.ShowDialog() != true) return;
        var commit = tip.StandardOutput;
        var url = string.IsNullOrWhiteSpace(AssetUrl) ? null : AssetUrl.Trim();
        var note = string.IsNullOrWhiteSpace(AssetRedistributionNote) ? null : AssetRedistributionNote.Trim();
        if (MessageBox.Show($"素材 {expectedName}\n保存先 {pool.FolderName}\n使用コミット {commit}\n\n選択したフォルダーに素材と記録を保存しますか？",
                "素材プールの確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var verifiedTip = await location.Value.Repository.InspectCurrentCommitAsync(token);
        if (!ReferenceEquals(project, SelectedProject) || project.FullPath != path || !verifiedTip.Success || verifiedTip.StandardOutput != commit)
        {
            AssetStatus = "選択中のプロジェクトまたはGitのコミットが変わりました。収集前に確認し直してください。";
            return;
        }
        var item = await Task.Run(async () =>
        {
            if (!string.Equals(Path.GetFileName(picker.FileName), expectedName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("選択した素材と元の素材参照の名前が一致しません。");
            using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(picker.FileName)!, enforceShare: true);
            using var handle = OwnedFilePublication.OpenOrdinaryPath(picker.FileName, expectDirectory: false, allowRename: false);
            await using var file = new FileStream(handle, FileAccess.Read);
            var sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, token));
            // Persist this exact payload before publishing the pool copy, including after later restarts.
            await location.Value.Repository.RememberPublicationAssetAsync(expectedName, sha, token);
            return await AssetPool.CollectAsync(pool.FolderName, picker.FileName, references[0], commit, url, note, token);
        }, token);
        lastApprovedPoolRoot = pool.FolderName;
        collectedPoolAssets.Add(item.Record);
        AssetStatus = $"収集しました。\n{item.Record.DistributionFileName}\nSHA-256 {item.Record.Sha256}\n記録 {item.ManifestPath}";
    });

    private string assetRedistributionNote = "";
    public string AssetRedistributionNote
    {
        get => assetRedistributionNote;
        set => SetProperty(ref assetRedistributionNote, value);
    }

    public sealed record GitChange(string Code, string Path, string? OriginalPath)
    {
        public string Display => OriginalPath is null ? $"{Code}  {Path}" : $"{Code}  {OriginalPath} → {Path}";
    }

    public ObservableCollection<GitChange> GitChanges { get; } = [];

    public GitChange? SelectedGitChange
    {
        get => selectedGitChange;
        set
        {
            if (SetProperty(ref selectedGitChange, value) && value is not null) GitPath = value.Path;
        }
    }
    private GitChange? selectedGitChange;

    private void UpdateGitChanges(string porcelain)
    {
        var entries = porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        GitChanges.Clear();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4 || entry[2] != ' ') continue;
            var code = entry[..2];
            var origin = (code.Contains('R') || code.Contains('C')) && i + 1 < entries.Length ? entries[++i] : null;
            GitChanges.Add(new GitChange(code, entry[3..], origin));
        }
    }

    private static bool ConfirmGitCommit(GitCommitTarget target, string message)
    {
        var window = new Window
        {
            Title = "ステージ済みの変更をコミット",
            Width = 680, Height = 500, MinWidth = 360, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.IsActive);
        if (owner is not null) window.Owner = owner;
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

    private static string DescribeCommitFailure(GitResult result) => result.Failure switch
    {
        GitFailure.MediaPublication => "素材、パッケージ、ローカル専用の回復記録が含まれるか、内容を確認できないためGit操作を中止しました。",
        GitFailure.ConcurrentOperation => "別のGit操作で状態が変わりました。変更一覧を更新し、再確認してください。",
        GitFailure.OutcomeUnknown => "コミットの結果を確認できません。再試行前に履歴とステージ済みの状態を確認してください。",
        _ => "Git操作を完了できません。リポジトリ、Gitの利用者設定、選択したファイル、ステージ済みの状態を確認してください。",
    };

    private async Task RegisterKnownPublicationAssetsAsync(GitRepository git, ProjectEntry project, CancellationToken token)
    {
        // Read the selected PF itself. Embedded paths are treated as names and are never opened.
        await using var file = ProjectFileEvidence.OpenRead(project.FullPath);
        if (file.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("素材混入の確認に対応するPFのサイズを超えています。");
        using var reader = new StreamReader(file);
        var text = await reader.ReadToEndAsync(token);
        using var parsed = System.Text.Json.JsonDocument.Parse(text);
        if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidDataException("素材混入を検査できるPFではありません。");
        var referencedNames = PackagingDetector.FindFilePaths(text);
        git.SetPublicationAssets(referencedNames, []);
        if (project.ProjectId is Guid id && project.MetadataAssociation is { } association)
        {
            var known = (await ResolveMetadataBranchAsync(id, association, token)).Selected.Manifest.Assets;
            git.SetPublicationAssets(known.Select(asset => asset.RelativePath), known.Select(asset => asset.Sha256));
        }
        git.SetPublicationAssets(collectedPoolAssets.SelectMany(asset => new[] { asset.OriginalFileName, asset.DistributionFileName }),
            collectedPoolAssets.Select(asset => asset.Sha256));
    }
}
