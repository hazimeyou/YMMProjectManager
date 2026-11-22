using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,method=FindAssetCandidatesAsync,素材候補一覧を探す（非同期）
    private async Task FindAssetCandidatesAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Asset candidates", async token =>
        {
            var source = selected.FullPath;
            var expected = selected.ProjectId is Guid id && selected.MetadataAssociation is { } association
                ? (await ResolveMetadataBranchAsync(id, association, token)).Selected.Manifest.Assets : null;
            //CSJ,variable=references,参照一覧
            var references = await AssetInventory.ScanProjectAsync(source, expected, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(selected, SelectedProject) || selected.FullPath != source) return;
            //CSJ,variable=rows,行一覧
            var rows = references.Select(reference =>
            {
                //CSJ,variable=status,状態
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
                //CSJ,variable=external,外部
                var external = reference.Expected?.ExternalLocation is { } location ? $" / 保管先 {location.StorageName}/{location.RelativePath}" : "";
                return $"{reference.Path}  {status}{actual}{prior}{external}";
            });
            AssetStatus = references.Count == 0 ? "FilePath 候補はありません。" :
                "FilePath 候補（素材とは限りません）\n" + string.Join("\n", rows);
        });
    }

    //CSJ,method=InspectAssetAsync,素材を点検する（非同期）
    private async Task InspectAssetAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteToolAsync("Asset inspection", async token =>
        {
            //CSJ,variable=directory,ディレクトリ
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            var requestedPath = AssetPath;
            var requestedUrl = AssetUrl;
            inspectedAssets = null;
            inspectedExternalRoot = null;
            //CSJ,variable=paths,パス一覧
            var paths = requestedPath.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0 || paths.Length > 1000) { AssetStatus = "素材パスを1〜1000件入力してください。"; return; }
            if (paths.Length != 1 && !string.IsNullOrWhiteSpace(requestedUrl)) { AssetStatus = "URL は素材を1件指定したときだけ入力できます。"; return; }
            //CSJ,variable=assets,素材一覧
            var assets = new List<AssetMetadata>(paths.Length);
            //CSJ,variable=path,パス
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

    //CSJ,method=ExportAssetMetadataAsync,素材メタデータを書き出す（非同期）
    private Task ExportAssetMetadataAsync() => SaveAssetMetadataAsync(false);

    //CSJ,method=SaveAssetMetadataAsync,素材メタデータを保存する（非同期）
    private async Task SaveAssetMetadataAsync(bool update)
    {
        var selected = SelectedProject;
        //CSJ,variable=assets,素材一覧
        var assets = inspectedAssets;
        //CSJ,variable=externalRoot,外部ルート
        var externalRoot = inspectedExternalRoot;
        if (selected is null || assets is null) { AssetStatus = "先に素材を検査してください。"; return; }
        //CSJ,variable=association,関連付け
        var association = update ? selected.MetadataAssociation : null;
        var source = selected.FullPath;
        //CSJ,variable=parentPath,親パス
        string? parentPath = null;
        if (update && association is null)
        {
            //CSJ,variable=existing,既存
            var existing = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "更新元の素材メタデータを選択", CheckFileExists = true };
            if (existing.ShowDialog() != true) return;
            parentPath = existing.FileName;
        }
        //CSJ,variable=dialog,ダイアログ
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", Title = "素材メタデータを新しいファイルに保存", OverwritePrompt = false };
        if (association is null && dialog.ShowDialog() != true) return;
        await ExecuteToolAsync("Asset metadata export", async token =>
        {
            if (HasUnsavedProjectList) { AssetStatus = "プロジェクト一覧に未保存の変更があります。先に一覧を保存してください。"; return; }
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            //CSJ,variable=directory,ディレクトリ
            var directory = Path.GetDirectoryName(Path.GetFullPath(source)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            //CSJ,variable=asset,素材
            foreach (var asset in assets)
                if (await VerifyInspectedAssetAsync(directory, externalRoot, asset, token) != AssetVerification.Valid) { AssetStatus = $"素材が変更されたか見つかりません: {asset.RelativePath}"; return; }
            var verifiedAtUtc = DateTimeOffset.UtcNow;
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            //CSJ,variable=managed,管理対象
            var managed = association is not null && selected.ProjectId is Guid id ? await ResolveMetadataBranchAsync(id, association, token) : null;
            if (association is not null && (managed is null || !managed.Graph.Heads.Contains(managed.Selected.Receipt)))
                throw new InvalidDataException("現在の head を選択し直してから版を追加してください。");
            var parent = managed?.Selected.Manifest ?? (parentPath is null ? null : await AssetManifestStore.LoadAsync(parentPath, token));
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets)) { AssetStatus = "選択が変わりました。再検査してください。"; return; }
            if (parent is not null && MessageBox.Show($"更新元のプロジェクト名: {parent.ProjectFileName}\n選択中: {selected.FullPath}\n同名でも別プロジェクトの可能性があります。同一プロジェクトの版として作成しますか？", "素材メタデータ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            //CSJ,variable=changes,変更一覧
            var changes = parent is null ? [] : AssetManifestStore.FindChangedPaths(parent, assets);
            if (changes.Count > 0)
            {
                if (changes.Count > 10) { AssetStatus = "変更が10件を超えています。すべてを確認できるよう、10件以下に分けて更新してください。"; return; }
                //CSJ,variable=details,詳細
                var details = changes.Take(10).Select(path =>
                {
                    //CSJ,variable=normalized,正規化済み
                    var normalized = Path.GetFullPath(path, Path.GetTempPath());
                    //CSJ,variable=oldAsset,旧素材
                    var oldAsset = parent!.Assets.First(item => string.Equals(Path.GetFullPath(item.RelativePath, Path.GetTempPath()), normalized, StringComparison.OrdinalIgnoreCase));
                    //CSJ,variable=newAsset,新規素材
                    var newAsset = assets.First(item => string.Equals(Path.GetFullPath(item.RelativePath, Path.GetTempPath()), normalized, StringComparison.OrdinalIgnoreCase));
                    return $"{path}\n以前: {oldAsset.Size} bytes / {oldAsset.Sha256}\n今回: {newAsset.Size} bytes / {newAsset.Sha256}";
                });
                if (MessageBox.Show($"サイズまたは SHA-256 が以前と異なります:\n{string.Join("\n", details)}\n変更 {changes.Count} 件を新しい版に記録しますか？", "素材メタデータ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            if (!ReferenceEquals(selected, SelectedProject) || !ReferenceEquals(assets, inspectedAssets) ||
                selected.FullPath != source || HasUnsavedProjectList || (association is not null && selected.MetadataAssociation != association))
                throw new InvalidDataException("素材またはプロジェクトの選択が変更されました。");
            //CSJ,variable=asset,素材
            foreach (var asset in assets)
                if (await VerifyInspectedAssetAsync(directory, externalRoot, asset, token) != AssetVerification.Valid)
                    throw new InvalidDataException($"確認後に素材が変更されました。{asset.RelativePath}");
            //CSJ,variable=manifest,目録
            var manifest = AssetManifestStore.Create(source, assets, verifiedAtUtc, parent, confirmedProjectAssociation: parent is not null, confirmedHashChanges: changes.Count > 0, projectId: selected.ProjectId);
            if (association is not null && managed is not null)
            {
                //CSJ,variable=receipt,照合記録
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

    //CSJ,method=VerifyAssetMetadataAsync,素材メタデータを検証する（非同期）
    private async Task VerifyAssetMetadataAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        //CSJ,variable=dialog,ダイアログ
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "素材メタデータを選択", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        await ExecuteToolAsync("Asset metadata verification", async token =>
        {
            //CSJ,variable=directory,ディレクトリ
            var directory = Path.GetDirectoryName(Path.GetFullPath(selected.FullPath)) ?? throw new InvalidOperationException("プロジェクトフォルダーがありません。");
            //CSJ,variable=assets,素材一覧
            IReadOnlyList<AssetMetadata> assets;
            //CSJ,variable=associationVerified,関連付け検証済み
            var associationVerified = false;
            try
            {
                //CSJ,variable=manifest,目録
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
            //CSJ,variable=results,結果一覧
            var results = new List<string>();
            //CSJ,variable=asset,素材
            foreach (var asset in assets)
                results.Add($"{asset.RelativePath}: {await AssetInventory.VerifyAsync(directory, asset, token)}");
            if (!ReferenceEquals(selected, SelectedProject)) return;
            AssetStatus = (results.Count == 0 ? "メタデータに素材がありません。" : string.Join("\n", results)) + (associationVerified ? $"\nプロジェクト ID 一致: {selected.ProjectId}" : "\nプロジェクトとの ID 関連付けは未確認です。");
        });
    }
}
