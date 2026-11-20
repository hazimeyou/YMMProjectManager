using System.IO;
using System.Windows;
using System.Windows.Input;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand RecoverProjectListPublicationCommand { get; }

    private string projectListPublicationStatus = string.Empty;
    public string ProjectListPublicationStatus
    {
        get => projectListPublicationStatus;
        private set => SetProperty(ref projectListPublicationStatus, value);
    }

    private async Task RecoverProjectListPublicationAsync()
    {
        var reload = false;
        await ExecuteToolAsync("Asset interrupted list publication", async token =>
        {
            if (repository is not JsonProjectRepository json) return;
            ProjectListReplacement.Recovery? review;
            try { review = await json.InspectInterruptedPublicationAsync(token); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                logger.Error(ex, "The interrupted list publication could not be inspected.");
                ProjectListPublicationStatus = $"公開記録を確認できませんでした。元のファイルと記録を保全してください。\n{json.InterruptedPublicationPath}";
                return;
            }
            if (review is null)
            {
                ProjectListPublicationStatus = "未完了の一覧公開記録はありません。";
                return;
            }

            var action = review.State switch
            {
                ProjectListReplacement.RecoveryState.CanRestore => "退避済みの元の一覧を、空いている現用パスへ戻します。",
                ProjectListReplacement.RecoveryState.CanRestoreExternal => "外部版と確認した退避ファイルを、空いている現用パスへ戻します。保存候補は保全します。",
                ProjectListReplacement.RecoveryState.Published => "保存済みの一覧を保ち、公開記録を閉じます。",
                ProjectListReplacement.RecoveryState.ActivePreserved => "現用の元の一覧を保ち、公開記録を閉じます。",
                ProjectListReplacement.RecoveryState.Conflict => "外部から置換された現用一覧を保ち、元の一覧と保存候補も残します。",
                _ => "安全に復旧できる版を確認できませんでした。以下の記録を保全してください。",
            };
            var detail = $"{action}\n現用一覧 {review.ActivePath}\n退避先 {review.ParkedPath}\n保存候補 {review.CandidatePath}\n公開記録 {review.JournalPath}";
            if (review.ActiveVersion is { } active)
                detail += $"\n現用一覧 SHA-256 {active.Sha256}";
            if (review.ParkedVersion is { } parked)
                detail += $"\n退避ファイル SHA-256 {parked.Sha256}";
            ProjectListPublicationStatus = detail;
            if (review.State == ProjectListReplacement.RecoveryState.Unverifiable ||
                MessageBox.Show(detail, "中断された一覧保存の回復", MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;

            try { await json.RecoverInterruptedPublicationAsync(review, token); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.Error(ex, "The reviewed list publication could not be recovered.");
                ProjectListPublicationStatus = $"一覧を回復できませんでした。記録は保持されています。再確認してください。\n{review.JournalPath}";
                return;
            }

            reload = !HasUnsavedProjectList;
            if (reload) isInitialized = false;
            ProjectListPublicationStatus = HasUnsavedProjectList
                ? "公開記録を確認しました。未保存の画面変更は保持しています。「一覧変更の競合を確認」を使ってください。"
                : "公開記録を確認しました。現用一覧を読み込みます。";
        });
        if (reload) await InitializeAsync();
    }
}
