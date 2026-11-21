using System.IO;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Infrastructure.Packaging;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    private async Task PackageSelectedProjectAsync()
    {
        var path = SelectedProject?.FullPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await PackageProjectAsync(path).ConfigureAwait(true);
    }

    private async Task PackageOpenedProjectAsync()
    {
        var currentProjectPath = YmmProjectPathResolver.TryGetCurrentProjectPath();
        if (string.IsNullOrWhiteSpace(currentProjectPath))
        {
            MessageBox.Show("現在 YMM で開いているプロジェクトを取得できませんでした。", "ymmpx", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await PackageProjectAsync(currentProjectPath).ConfigureAwait(true);
    }

    private Task ExtractBundleAsync() => ExecuteToolAsync("Bundle extraction", async token =>
    {
        var bundle = new OpenFileDialog
        {
            Filter = "YMM同梱ファイル (*.ymmpx)|*.ymmpx",
            Title = "展開する ymmpx を選択",
            CheckFileExists = true,
        };
        if (bundle.ShowDialog() != true) return;

        var parent = new OpenFolderDialog { Title = "新しい展開フォルダーを作成する場所を選択" };
        if (parent.ShowDialog() != true) return;
        var destination = Path.Combine(parent.FolderName, Path.GetFileNameWithoutExtension(bundle.FileName));
        if (MessageBox.Show($"次の場所に新しいフォルダーを作成します。\n{destination}\n\n新しい受け渡し用YMMPXは、同じフォルダーの ymmpm-transfer.json と照合します。従来のYMMPXでは素材名とSHA-256の一致は証明できません。",
            "ymmpx 展開", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        BundleStatus = "ymmpx を展開中...";
        BundleProgress = 0;
        var progress = new Progress<double>(value => BundleProgress = value * 100d);
        var result = await bundleService.ExtractPackageAsync(bundle.FileName, destination, token, progress);
        if (!result.Success)
        {
            BundleStatus = result.ErrorMessage ?? "展開に失敗しました。";
            MessageBox.Show(BundleStatus, "ymmpx", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BundleProgress = 100;
        BundleStatus = $"展開完了 {result.RestoredProjectPath}\n素材参照の更新 {result.ReplacedPathCount} 件";
        BundleStatus += result.TransferEvidenceVerified
            ? $"\n素材名・サイズ・SHA-256 照合済み {result.ExtractedResourceCount} 件"
            : "\n従来形式で展開しました。素材名・SHA-256 の照合記録はありません。";
        if (result.MissingMaterials.Count > 0)
        {
            BundleStatus += $"\n不足・同梱対象外 {result.MissingMaterials.Count} 件";
            MessageBox.Show(BundleStatus + "\n\n" + string.Join("\n", result.MissingMaterials.Take(20)) +
                (result.MissingMaterials.Count > 20 ? "\n（先頭20件）" : ""),
                "YMMPX の不足素材", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        if (isInitialized && !HasUnsavedProjectList && retainedRecoverySnapshot is null &&
            result.RestoredProjectPath is { } restored && result.RestoredProjectIdentity is { } extractedIdentity &&
            MessageBox.Show($"{BundleStatus}\n一覧に新しいプロジェクトとして登録しますか？",
                "ymmpx 展開", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            try { await RegisterExtractedProjectAsync(restored, extractedIdentity, token); }
            catch
            {
                BundleStatus += "\n一覧への登録に失敗しました。展開済みファイルは保持されています。";
                throw;
            }
        }
    });

    private async Task RegisterExtractedProjectAsync(string restoredYmmpPath, ProjectFileIdentity extractedIdentity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(restoredYmmpPath);
        if (Projects.Any(x => string.Equals(Path.GetFullPath(x.FullPath), fullPath, StringComparison.OrdinalIgnoreCase))) return;
        using var directory = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(fullPath)!, enforceShare: true);
        await using var file = YmmpxTransfer.OpenOrdinaryRead(fullPath);
        var current = await ProjectFileEvidence.ReadAsync(file, token);
        if (current != extractedIdentity)
            throw new IOException("展開後にPFが変更されたため、一覧に登録できません。");
        if (Projects.Any(project => project.FileIdentity is { } known && known.IsSameFile(current)))
            throw new IOException("同じPFが既に一覧にあります。");
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var entry = new ProjectEntry
        {
            FullPath = fullPath,
            ProjectId = id,
            DisplayName = Path.GetFileNameWithoutExtension(fullPath),
            FileIdentity = current,
        };
        pendingAdditionEvidence[fullPath] = (id, current.Sha256);
        pendingExtractedProject = (id, fullPath, current);
        entry.LinkedYmmpFiles.Add(new LinkedYmmpFile { FilePath = fullPath, DisplayName = entry.DisplayName,
            Role = YmmpRole.Main, Exists = true, LastCheckedAt = DateTimeOffset.Now });
        Projects.Add(entry);
        SelectedProject = entry;
        await SaveAsync();
        PrepareThumbnailMetadata([entry]);
    }
}
