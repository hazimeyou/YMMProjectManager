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
        if (previous is not null && !IsClaimedDraft(previous)) drafts.Retire(previous);
    }

    private async Task SaveRepositoryWithPendingDraftAsync(ProjectStore store)
    {
        if (pendingDraftPath is not { } pending || repository is not JsonProjectRepository json)
        {
            await repository.SaveAsync(store);
            return;
        }
        var drafts = new ProjectListDrafts(json.RecoveryDirectory);
        if (!IsClaimedDraft(pending)) pendingDraftPath = drafts.Claim(pending);
        try { await json.SaveWithDraftAsync(store, pendingDraftPath); }
        catch (Exception ex) when (ex is ProjectListConflictException or ProjectListBusyException)
        {
            pendingDraftPath = ReleaseUnpublishedClaim(drafts, pendingDraftPath);
            throw;
        }
        ClosePendingDraft();
    }

    private string ReleaseUnpublishedClaim(ProjectListDrafts drafts, string path)
    {
        try { return drafts.ReleaseUnpublishedClaim(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error(ex, "Recovery record remains available for export.");
            return path;
        }
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

    private void ClosePendingDraft()
    {
        if (pendingDraftPath is not { } path || repository is not JsonProjectRepository json) return;
        pendingDraftPath = null;
        try { new ProjectListDrafts(json.RecoveryDirectory).Retire(path, claimed: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.Error(ex, "Recovery draft retirement deferred."); }
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
                path = path[..^7];
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

        var originalPaths = draft.Baseline.Projects.Select(p => p.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var addition in draft.Local.Projects.Where(p => !originalPaths.Contains(p.FullPath)))
        {
            if (!draft.Additions.TryGetValue(addition.FullPath, out var evidence) || evidence.Id != addition.ProjectId ||
                await HashRecoveryFileAsync(addition.FullPath) != evidence.Hash)
                throw new InvalidDataException("追加プロジェクトの元の内容と一致しません。");
        }
        if (savedBaseline is null || CurrentStoreFingerprint() != ProjectListRecovery.Fingerprint(savedBaseline))
            throw new InvalidDataException("画面上の一覧が変更されました。");
        var screen = CurrentStoreFingerprint();
        var claim = drafts.Claim(path);
        try { await json.SaveWithDraftAsync(plan.Store, claim); }
        catch (Exception ex) when (ex is ProjectListConflictException or ProjectListBusyException)
        {
            ReleaseUnpublishedClaim(drafts, claim);
            throw;
        }
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
        try { drafts.Retire(claim, claimed: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.Error(ex, "Recovery draft retirement deferred."); }
        PrepareThumbnailMetadata(Projects.ToList());
        AssetStatus = "前回の保存記録から一覧を回復しました。";
    });
}
