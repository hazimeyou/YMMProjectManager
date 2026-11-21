using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand CollectAssetToPoolCommand { get; }

    private string? lastApprovedPoolRoot;
    private readonly List<AssetPoolRecord> collectedPoolAssets = [];

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
            var sha = await AssetContentHash.ReadSha256Async(file, token);
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
}
