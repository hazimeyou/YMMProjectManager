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
    private string? pendingDraftPath;

    private async Task SavePendingDraftAsync(ProjectStore local)
    {
        if (repository is not JsonProjectRepository json || savedBaseline is not { IsRecoverySafe: true } baseline ||
            ProjectListRecovery.Fingerprint(baseline) == ProjectListRecovery.Fingerprint(local)) return;
        var drafts = new ProjectListDrafts(json.RecoveryDirectory);
        var evidence = pendingAdditionEvidence.ToDictionary(pair => pair.Key,
            pair => new ProjectListDrafts.Evidence(pair.Value.Id, pair.Value.Hash), StringComparer.OrdinalIgnoreCase);
        var next = await drafts.WriteAsync(baseline, local, evidence);
        pendingDraftPath = drafts.Claim(next);
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
        var claimed = picker.FileName.EndsWith(".saving", StringComparison.Ordinal);
        var draft = await drafts.ReadAsync(picker.FileName, claimed);
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
        var plan = ProjectListMerge.Merge(draft.Baseline, draft.Local, latest);
        if (plan.Store is null)
        {
            var conflict = string.Join("\n", plan.Conflicts.Take(20));
            if (MessageBox.Show($"変更が競合しています。保存記録を閉じますか？\n{conflict}", "前回の保存記録",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                drafts.Retire(picker.FileName);
            return;
        }
        var hasChanges = ProjectListRecovery.Fingerprint(plan.Store) != ProjectListRecovery.Fingerprint(latest);
        var summary = string.Join("\n", plan.Changes);
        if (summary.Length > 12000) throw new InvalidDataException("変更内容が大きすぎるため再適用できません。");
        var choice = MessageBox.Show(hasChanges
            ? $"次の保存記録を再適用しますか？\n{summary}\n\n「いいえ」で記録を閉じ、「キャンセル」で保持します。"
            : "再適用が必要な変更はありません。「いいえ」で記録を閉じ、「キャンセル」で保持します。",
            "前回の保存記録", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.No) { drafts.Retire(picker.FileName); return; }
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
        var claim = drafts.Claim(picker.FileName);
        await repository.SaveAsync(plan.Store);
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
        drafts.Retire(claim, claimed: true);
        PrepareThumbnailMetadata(Projects.ToList());
        AssetStatus = "前回の保存記録から一覧を回復しました。";
    });
}
