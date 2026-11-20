using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Application;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Presentation.Views;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectListViewModel
{
    public ICommand ReviewDraftCommand { get; }
    public ICommand ExportOriginalProjectListCommand { get; }
    private string? pendingDraftPath;

    private Task ExportOriginalProjectListAsync() => ExecuteToolAsync("Asset project list export", async token =>
    {
        if (repository is not JsonProjectRepository json) return;
        var picker = new SaveFileDialog { Filter = "JSON (*.json)|*.json", DefaultExt = ".json",
            Title = "ディスク上の一覧をそのまま別ファイルへ退避", OverwritePrompt = false };
        if (picker.ShowDialog() != true) return;
        await json.ExportOriginalAsync(picker.FileName, token);
        AssetStatus = $"原本の内容を新しいファイルへ退避しました。\n{picker.FileName}";
    });

    private async Task SavePendingDraftAsync(ProjectStore local)
    {
        if (repository is not JsonProjectRepository json || savedBaseline is not { IsRecoverySafe: true } baseline ||
            (pendingDraftPath is null && ProjectListRecovery.Fingerprint(baseline) == ProjectListRecovery.Fingerprint(local))) return;
        var evidence = pendingAdditionEvidence.ToDictionary(pair => pair.Key,
            pair => new ProjectListDrafts.Evidence(pair.Value.Id, pair.Value.Hash), StringComparer.OrdinalIgnoreCase);
        pendingDraftPath = await json.WritePendingDraftAsync(baseline, local, evidence, pendingDraftPath);
    }

    private async Task SaveRepositoryWithPendingDraftAsync(ProjectStore store)
    {
        if (pendingDraftPath is not { } pending || repository is not JsonProjectRepository json)
        {
            await repository.SaveAsync(store);
            return;
        }
        try { await json.SaveReadyDraftAsync(store, pending); }
        catch
        {
            if (!File.Exists(pending)) pendingDraftPath = null;
            throw;
        }
        pendingDraftPath = null;
    }

    private static bool IsClaimedDraft(string path) => path.EndsWith(".saving", StringComparison.Ordinal);

    private async Task ExportDraftCopyAsync(ProjectStore local, string title)
    {
        var export = new SaveFileDialog { Filter = "JSON (*.json)|*.json", DefaultExt = ".json", OverwritePrompt = false, Title = title };
        if (export.ShowDialog() != true) return;
        await new JsonProjectRepository(logger, export.FileName).SaveNewCopyAsync(local);
        AssetStatus = "未保存の一覧を新しいファイルに退避しました。現在の一覧と保存記録は保持されています。";
    }

    private static ProjectListMergeResult? MergeWithReview(ProjectStore baseline, ProjectStore local, ProjectStore remote)
    {
        var plan = ProjectListMerge.Merge(baseline, local, remote);
        if (!plan.CanResolveAll || plan.Choices.Count > 20 ||
            plan.Choices.Any(conflict => conflict.Local.Length > 32000 || conflict.Remote.Length > 32000)) return plan;
        var window = new ProjectListConflictReview(plan.Choices, GetActiveWindow());
        if (window.ShowDialog() != true || !window.AllDecisionsMade) return null;
        var decisions = window.Decisions;
        var resolved = ProjectListMerge.Merge(baseline, local, remote, decisions);
        if (resolved.Store is null) return resolved;
        var summary = decisions.Select(pair =>
            $"{pair.Key.Item} / {pair.Key.Field}  {(pair.Value ? "未保存の値を採用" : "ディスク上の値を採用")}");
        return resolved with { Changes = summary.Concat(resolved.Changes).ToArray() };
    }

    private Task ReviewDraftAsync() => ExecuteWithBusyAsync("Recover project list", async () =>
    {
        if (repository is not JsonProjectRepository json || HasUnsavedProjectList) return;
        var drafts = new ProjectListDrafts(json.RecoveryDirectory);
        var picker = new OpenFileDialog { InitialDirectory = drafts.DirectoryPath,
            Filter = "一覧の保存記録 (*.draft.json;*.saving)|*.draft.json;*.saving|終了した記録 (*.closed)|*.closed" };
        if (picker.ShowDialog() != true) return;
        var path = picker.FileName;
        if (path.EndsWith(".closed", StringComparison.Ordinal))
        {
            var archived = await drafts.ReadArchivedAsync(path);
            await ExportDraftCopyAsync(archived.Local, "終了した一覧の記録を別ファイルに退避");
            return;
        }
        var claimed = IsClaimedDraft(path);
        var (draft, exportOnly) = await drafts.ReadForReviewAsync(path, claimed);
        if (exportOnly)
        {
            await ExportDraftCopyAsync(draft.Local, "より新しい記録または競合する記録があるため、選択した一覧を別ファイルに退避");
            return;
        }
        if (claimed)
        {
            var outcome = ProjectListPublications.Outcome.Unknown;
            try { outcome = await json.ReconcileDraftAsync(path); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            { logger.Error(ex, "Preserved an invalid project list publication receipt."); }
            if (outcome == ProjectListPublications.Outcome.Committed)
            {
                AssetStatus = "一覧ファイルから保存済みであることを確認しました。再適用は不要です。";
                return;
            }
            if (outcome == ProjectListPublications.Outcome.Unpublished)
            {
                path = json.GetReadyDraftPath(path);
                claimed = false;
            }
        }
        if (claimed)
        {
            await ExportDraftCopyAsync(draft.Local, "保存の成否が不明なため、未保存候補を別ファイルに退避");
            return;
        }
        var latest = await repository.LoadAsync();
        var plan = MergeWithReview(draft.Baseline, draft.Local, latest);
        if (plan is null) return;
        if (plan.Store is null)
        {
            var conflict = string.Join("\n", plan.Conflicts.Take(20));
            var decision = MessageBox.Show($"安全に再適用できない競合があります。\n{conflict}\n\n「はい」で未保存の一覧を別名退避し、「いいえ」で記録を閉じます。「キャンセル」で記録を保持します。",
                "前回の保存記録", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (decision == MessageBoxResult.Yes) await ExportDraftCopyAsync(draft.Local, "未保存の一覧を別ファイルに退避");
            if (decision == MessageBoxResult.No) await json.RetireReadyDraftAsync(path);
            return;
        }
        var hasChanges = ProjectListRecovery.Fingerprint(plan.Store) != ProjectListRecovery.Fingerprint(latest);
        var summary = string.Join("\n", plan.Changes);
        if (summary.Length > 12000) throw new InvalidDataException("変更内容が大きすぎるため再適用できません。");
        var choice = MessageBox.Show(hasChanges
            ? $"次の保存記録を再適用しますか？\n{summary}\n\n「いいえ」で記録を閉じ、「キャンセル」で保持します。"
            : "再適用が必要な変更はありません。「いいえ」で記録を閉じ、「キャンセル」で保持します。",
            "前回の保存記録", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.No) { await json.RetireReadyDraftAsync(path); return; }
        if (choice != MessageBoxResult.Yes || !hasChanges) return;

        using var verifiedFiles = await ProjectListRecoveryFiles.OpenAsync(plan.Store, latest,
            path => draft.Additions.TryGetValue(path, out var evidence) ? (evidence.Id, evidence.Hash) : null);
        if (savedBaseline is null || CurrentStoreFingerprint() != ProjectListRecovery.Fingerprint(savedBaseline))
            throw new InvalidDataException("画面上の一覧が変更されました。");
        var screen = CurrentStoreFingerprint();
        await json.SaveReadyDraftAsync(plan.Store, path);
        if (screen != CurrentStoreFingerprint())
        {
            FreezePartialRecovery(draft.Local, "再適用の保存中に画面の一覧が変わりました。");
            return;
        }
        if (!AdoptRecoveredStore(plan.Store, draft.Local, null)) return;
        savedBaseline = ProjectListMerge.Snapshot(plan.Store);
        projectListRevision = plan.Store.Revision;
        isProjectListLossless = plan.Store.IsRecoverySafe;
        lastSavedFingerprint = CurrentStoreFingerprint();
        PrepareThumbnailMetadata(Projects.ToList());
        AssetStatus = "前回の保存記録から一覧を回復しました。";
    });
}
