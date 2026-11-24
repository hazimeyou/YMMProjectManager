using System.IO;
using System.Windows;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Presentation.Relink;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ProjectListViewModel,プロジェクト一覧の表示モデル
public sealed partial class ProjectListViewModel
{
    //CSJ,method=OpenSelectedProjectRelinkAsync,選択中のプロジェクト再リンクを開く（非同期）
    private async Task OpenSelectedProjectRelinkAsync()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        await ExecuteWithBusyAsync("Selected project relink", async () =>
        {
            if (!TryGetOpenableProjectPath(selected.FullPath, out var projectPath, out var reason))
            {
                MessageBox.Show(reason, "再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (TimelineContextService.Timeline is not null)
            {
                MessageBox.Show("タイムラインが開いている間はファイル側の再リンクを行いません。タイムライン用の再リンクを使用してください。", "再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            //CSJ,variable=resolve,解決
            Func<CancellationToken, Task<AssetRevisionSeries.BranchSelection>>? resolve = null;
            //CSJ,variable=seriesDirectory,系列ディレクトリ
            string? seriesDirectory = null;
            if (selected.ProjectId is Guid id && selected.MetadataAssociation is { } association)
            {
                if (selected.FileIdentity is { } observed && !observed.IsSameFile(await ProjectFileEvidence.ReadAsync(projectPath)))
                    throw new InvalidDataException("登録時と異なるファイルです。「識別情報を照合」から確認してください。");
                seriesDirectory = association.Directory;
                resolve = token => ResolveMetadataBranchAsync(id, association, token);
            }
            //CSJ,variable=dispatcher,ディスパッチャー
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            OpenRelinkWindowDialog(new RelinkMainWindow(projectPath, logger, resolve, seriesDirectory)
            {
                ExpectedSourceIdentity = selected.FileIdentity,
                TransferAssociationAsync = selected.MetadataAssociation is { } metadata
                    ? (receipt, token) => ApplyRelinkOutputAsync(selected, metadata, receipt, token) : null,
                RegisterDerivedAsync = selected.MetadataAssociation is { } sourceMetadata
                    ? (receipt, token) => dispatcher.InvokeAsync(() => ApplyDerivedRelinkAsync(selected, sourceMetadata, receipt, token)).Task.Unwrap()
                    : null,
            });
        });
    }

    //CSJ,method=OpenOpenedProjectRelinkWindowAsync,表示済みプロジェクト再リンクウィンドウを開く（非同期）
    private async Task OpenOpenedProjectRelinkWindowAsync()
    {
        await ExecuteWithBusyAsync("OpenOpenedProjectRelinkWindow", () =>
        {
            //CSJ,variable=info,情報
            var info = TimelineContextService.Info;
            if (info?.Timeline is null)
            {
                MessageBox.Show("開いているPFが見つかりませんでした。", "素材再リンク", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Task.CompletedTask;
            }

            //CSJ,variable=projectPath,プロジェクトのパス
            var projectPath = YmmProjectPathResolver.TryGetCurrentProjectPath();
            if (!string.IsNullOrWhiteSpace(projectPath) &&
                !TryGetOpenableProjectPath(projectPath, out projectPath, out _))
            {
                projectPath = null;
            }

            OpenRelinkWindowDialog(new RelinkMainWindow(info, logger, projectPath));
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    //CSJ,method=OpenRelinkWindowDialog,再リンクウィンドウダイアログを開く
    private void OpenRelinkWindowDialog(RelinkMainWindow window)
    {
        var owner = GetActiveWindow();
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.ShowDialog();
    }
}
