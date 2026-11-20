using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YMMProjectManager.Application;
using YMMProjectManager.Infrastructure;

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
            ProjectListRecovery.Fingerprint(baseline) == ProjectListRecovery.Fingerprint(local)) return;
        var drafts = new ProjectListDrafts(json.RecoveryDirectory);
        var evidence = pendingAdditionEvidence.ToDictionary(pair => pair.Key,
            pair => new ProjectListDrafts.Evidence(pair.Value.Id, pair.Value.Hash), StringComparer.OrdinalIgnoreCase);
        var previous = pendingDraftPath;
        var next = await drafts.WriteAsync(baseline, local, evidence);
        pendingDraftPath = next;
        if (previous is not null && File.Exists(previous)) drafts.Retire(previous);
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

    private static ProjectListMergeResult? MergeWithReview(ProjectStore baseline, ProjectStore local, ProjectStore remote)
    {
        var plan = ProjectListMerge.Merge(baseline, local, remote);
        if (!plan.CanResolveAll || plan.Choices.Count > 12) return plan;
        var decisions = new Dictionary<ProjectListConflict, bool>();
        foreach (var conflict in plan.Choices)
        {
            var display = $"{conflict.Item} / {conflict.Field}\n\n未保存の値\n{conflict.Local}\n\nディスク上の値\n{conflict.Remote}";
            if (display.Length > 8000) return plan;
            var decision = MessageBox.Show(display + "\n\n「はい」で未保存の値、「いいえ」でディスク上の値を選びます。",
                "一覧の競合を解決", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (decision == MessageBoxResult.Cancel) return null;
            decisions.Add(conflict, decision == MessageBoxResult.Yes);
        }
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
            Filter = "一覧の保存記録 (*.draft.json;*.saving)|*.draft.json;*.saving" };
        if (picker.ShowDialog() != true) return;
        var path = picker.FileName;
        var claimed = IsClaimedDraft(path);
        var draft = await drafts.ReadAsync(path, claimed);
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
            var export = new SaveFileDialog { Filter = "JSON (*.json)|*.json", DefaultExt = ".json", OverwritePrompt = false,
                Title = "保存の成否が不明なため、未保存候補を別ファイルに退避" };
            if (export.ShowDialog() == true)
            {
                await new JsonProjectRepository(logger, export.FileName).SaveNewCopyAsync(draft.Local);
                AssetStatus = "一覧候補を新しいファイルに退避しました。現在の一覧と保存記録は保持されています。";
            }
            return;
        }
        var latest = await repository.LoadAsync();
        var plan = MergeWithReview(draft.Baseline, draft.Local, latest);
        if (plan is null) return;
        if (plan.Store is null)
        {
            var conflict = string.Join("\n", plan.Conflicts.Take(20));
            if (MessageBox.Show($"変更が競合しています。保存記録を閉じますか？\n{conflict}", "前回の保存記録",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                drafts.Retire(path);
            return;
        }
        var hasChanges = ProjectListRecovery.Fingerprint(plan.Store) != ProjectListRecovery.Fingerprint(latest);
        var summary = string.Join("\n", plan.Changes);
        if (summary.Length > 12000) throw new InvalidDataException("変更内容が大きすぎるため再適用できません。");
        var choice = MessageBox.Show(hasChanges
            ? $"次の保存記録を再適用しますか？\n{summary}\n\n「いいえ」で記録を閉じ、「キャンセル」で保持します。"
            : "再適用が必要な変更はありません。「いいえ」で記録を閉じ、「キャンセル」で保持します。",
            "前回の保存記録", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.No) { drafts.Retire(path); return; }
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
