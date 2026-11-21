using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    private async Task FindAssetCandidatesAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Asset candidates", async token =>
        {
            var source = selected.FullPath;
            var expected = selected.ProjectId is Guid id && selected.MetadataAssociation is { } association
                ? (await ResolveMetadataBranchAsync(id, association, token)).Selected.Manifest.Assets : null;
            var references = await AssetInventory.ScanProjectAsync(source, expected, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(selected, SelectedProject) || selected.FullPath != source) return;
            var rows = references.Select(reference =>
            {
                var status = reference.Status switch
                {
                    AssetReferenceStatus.Present => "ファイルあり",
                    AssetReferenceStatus.Missing => "不足",
                    AssetReferenceStatus.Changed => "変更あり",
                    AssetReferenceStatus.External => "プロジェクト外",
                    _ => "検査できません",
                };
                var actual = reference.Actual is { } asset ? $" / {asset.Size} bytes / SHA-256 {asset.Sha256}" : "";
                var prior = reference.Expected is { } known ? $" / 期待 {known.Size} bytes / SHA-256 {known.Sha256}" : "";
                var external = reference.Expected?.ExternalLocation is { } location ? $" / 保管先 {location.StorageName}/{location.RelativePath}" : "";
                return $"{reference.Path}  {status}{actual}{prior}{external}";
            });
            AssetStatus = references.Count == 0 ? "FilePath 候補はありません。" :
                "FilePath 候補（素材とは限りません）\n" + string.Join("\n", rows);
        });
    }

    private async Task InspectAssetAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Asset inspection", async token =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            var requestedPath = AssetPath;
            var requestedUrl = AssetUrl;
            inspectedAssets = null;
            inspectedExternalRoot = null;
            var paths = requestedPath.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0 || paths.Length > 1000) { AssetStatus = "素材パスを1〜1000件入力してください。"; return; }
            if (paths.Length != 1 && !string.IsNullOrWhiteSpace(requestedUrl)) { AssetStatus = "URL は素材を1件指定したときだけ入力できます。"; return; }
            var assets = new List<AssetMetadata>(paths.Length);
            foreach (var path in paths)
            {
                try { assets.Add(await AssetInventory.InspectAsync(directory, path, string.IsNullOrWhiteSpace(requestedUrl) ? null : requestedUrl, token)); }
                catch (FileNotFoundException) { AssetStatus = $"素材が見つかりません: {path}"; return; }
                catch (DirectoryNotFoundException) { AssetStatus = $"素材が見つかりません: {path}"; return; }
            }
            if (!ReferenceEquals(selected, SelectedProject) || AssetPath != requestedPath || AssetUrl != requestedUrl) return;
            inspectedAssets = assets;
            AssetStatus = string.Join("\n", assets.Select(asset => $"{asset.RelativePath}: {asset.Size} bytes SHA-256: {asset.Sha256}"));
        });
    }

    private Task ExportAssetMetadataAsync() => SaveAssetMetadataAsync(false);

    private async Task SaveAssetMetadataAsync(bool update)
    {
        var selected = SelectedProject;
        var assets = inspectedAssets;
        var externalRoot = inspectedExternalRoot;
        if (selected is null || assets is null) { AssetStatus = "先に素材を検査してください。"; return; }
        var association = update ? selected.MetadataAssociation : null;
        var source = selected.FullPath;
        string? parentPath = null;
        if (update && association is null)
        {
            var existing = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "更新元の素材メタデータを選択", CheckFileExists = true };
            if (existing.ShowDialog() != true) return;
            parentPath = existing.FileName;
        }
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", Title = "素材メタデータを新しいファイルに保存", OverwritePrompt = false };
        if (association is null && dialog.ShowDialog() != true) return;
        await ExecuteToolAsync("Asset metadata export", async token =>
        {
            if (HasUnsavedProjectList) { AssetStatus = "プロジェクト一覧に未保存の変更があります。先に一覧を保存してください。"; return; }
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            var directory = Path.GetDirectoryName(Path.GetFullPath(source)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            foreach (var asset in assets)
                if (await VerifyInspectedAssetAsync(directory, externalRoot, asset, token) != AssetVerification.Valid) { AssetStatus = $"素材が変更されたか見つかりません: {asset.RelativePath}"; return; }
            var verifiedAtUtc = DateTimeOffset.UtcNow;
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            var managed = association is not null && selected.ProjectId is Guid id ? await ResolveMetadataBranchAsync(id, association, token) : null;
            if (association is not null && (managed is null || !managed.Graph.Heads.Contains(managed.Selected.Receipt)))
                throw new InvalidDataException("現在の head を選択し直してから版を追加してください。");
            var parent = managed?.Selected.Manifest ?? (parentPath is null ? null : await AssetManifestStore.LoadAsync(parentPath, token));
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            if (parent is not null && MessageBox.Show($"更新元のプロジェクト名: {parent.ProjectFileName}\n選択中: {selected.FullPath}\n同名でも別プロジェクトの可能性があります。同一プロジェクトの版として作成しますか？", "素材メタデータ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var changes = parent is null ? [] : AssetManifestStore.FindChangedPaths(parent, assets);
            if (changes.Count > 0)
            {
                if (changes.Count > 10) { AssetStatus = "変更が10件を超えています。すべてを確認できるよう、10件以下に分けて更新してください。"; return; }
                var details = changes.Take(10).Select(path =>
                {
                    var normalized = Path.GetFullPath(path, Path.GetTempPath());
                    var oldAsset = parent!.Assets.First(item => string.Equals(Path.GetFullPath(item.RelativePath, Path.GetTempPath()), normalized, StringComparison.OrdinalIgnoreCase));
                    var newAsset = assets.First(item => string.Equals(Path.GetFullPath(item.RelativePath, Path.GetTempPath()), normalized, StringComparison.OrdinalIgnoreCase));
                    return $"{path}\n以前: {oldAsset.Size} bytes / {oldAsset.Sha256}\n今回: {newAsset.Size} bytes / {newAsset.Sha256}";
                });
                if (MessageBox.Show($"サイズまたは SHA-256 が以前と異なります:\n{string.Join("\n", details)}\n変更 {changes.Count} 件を新しい版に記録しますか？", "素材メタデータ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets) ||
                selected.FullPath != source || HasUnsavedProjectList || (association is not null && selected.MetadataAssociation != association))
                throw new InvalidDataException("素材またはプロジェクトの選択が変更されました。");
            foreach (var asset in assets)
                if (await VerifyInspectedAssetAsync(directory, externalRoot, asset, token) != AssetVerification.Valid)
                    throw new InvalidDataException($"確認後に素材が変更されました。{asset.RelativePath}");
            var manifest = AssetManifestStore.Create(source, assets, verifiedAtUtc, parent, confirmedProjectAssociation: parent is not null, confirmedHashChanges: changes.Count > 0, projectId: selected.ProjectId);
            if (association is not null && managed is not null)
            {
                var receipt = await AssetRevisionSeries.AppendToBranchAsync(association.Directory, managed.Graph.Root,
                    managed.Graph.Heads, managed.Selected.Receipt, manifest, token);
                AssetStatus = $"履歴に版を追加しました。{receipt.RevisionId}\n使用する版は「履歴の版を選択」から変更できます。";
                return;
            }
            await AssetManifestStore.SaveNewAsync(dialog.FileName, manifest, token);
            AssetStatus = selected.ProjectId is null
                ? "素材メタデータを保存しました。旧プロジェクトとの永続 ID 関連付けは未設定です。"
                : "素材メタデータを新しいファイルに保存しました。元のメタデータは変更していません。";
        });
    }

    private async Task VerifyAssetMetadataAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "素材メタデータを選択", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        await ExecuteToolAsync("Asset metadata verification", async token =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            IReadOnlyList<AssetMetadata> assets;
            var associationVerified = false;
            try
            {
                var manifest = await AssetManifestStore.LoadAsync(dialog.FileName, token);
                if (selected.ProjectId is Guid projectId && manifest.ProjectId != projectId)
                { AssetStatus = "メタデータのプロジェクト ID が一致しません。"; return; }
                associationVerified = selected.ProjectId is not null;
                if (selected.ProjectId is null && !string.Equals(manifest.ProjectFileName, Path.GetFileName(selected.FullPath), StringComparison.OrdinalIgnoreCase))
                { AssetStatus = "メタデータのプロジェクト名が一致しません。"; return; }
                assets = manifest.Assets;
            }
            catch (JsonException)
            {
                // R2 exported a bare array; retain read compatibility without rewriting it.
                assets = await AssetInventory.LoadAsync(dialog.FileName, token);
            }
            var results = new List<string>();
            foreach (var asset in assets)
                results.Add($"{asset.RelativePath}: {await AssetInventory.VerifyAsync(directory, asset, token)}");
            if (!ReferenceEquals(selected, SelectedProject)) return;
            AssetStatus = (results.Count == 0 ? "メタデータに素材がありません。" : string.Join("\n", results)) + (associationVerified ? $"\nプロジェクト ID 一致: {selected.ProjectId}" : "\nプロジェクトとの ID 関連付けは未確認です。");
        });
    }
}
