using System.IO;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Presentation.Relink;

//CSJ,class=RelinkMainWindow,素材の再リンク画面
public partial class RelinkMainWindow
{
    //CSJ,class=DerivedRegistrationException,派生プロジェクト登録例外
    private sealed class DerivedRegistrationException(Exception inner) : IOException("The derived output was verified before registration.", inner);
    //CSJ,record=StrictMatch,厳格一致
    private sealed record StrictMatch(string ExpectedName, string ExpectedSha256, string ApprovedRoot,
        bool HasExternalMetadata, AssetPoolMatch Candidate);
    //CSJ,field=strictCandidates,厳格候補一覧
    private readonly Dictionary<int, StrictMatch[]> strictCandidates = [];
    private AssetRevisionSeries.Head? searchedRevision;

    //CSJ,method=SearchStrictAsync,厳格を検索する（非同期）
    private async Task SearchStrictAsync()
    {
        if (context is null || rows.Count == 0) return;
        if (resolveMetadata is null)
        {
            SetSummaryText("先にこのプロジェクトの素材メタデータを関連付けてください。期待SHA-256がない参照は変更できません。");
            return;
        }
        //CSJ,variable=mode,モード
        var mode = MessageBox.Show("配布名を元の名前に照合する素材プールの記録を使用しますか？\n「いいえ」で元の名前の素材をフォルダーから探します。",
            "素材の照合", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (mode == MessageBoxResult.Cancel) return;
        //CSJ,variable=root,ルート
        var root = new OpenFolderDialog { Title = "検索を許可するフォルダーを選択" };
        if (root.ShowDialog() != true) return;
        //CSJ,variable=manifestPath,目録パス
        string? manifestPath = null;
        if (mode == MessageBoxResult.Yes)
        {
            var select = new OpenFileDialog
            {
                Title = "検索を許可した場所の素材プール記録を選択",
                Filter = "素材プールの記録|asset-pool.json",
                CheckFileExists = true,
                InitialDirectory = root.FolderName,
            };
            if (select.ShowDialog() != true) return;
            manifestPath = select.FileName;
        }

        //CSJ,variable=cancellation,キャンセル
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        searchCts = cancellation;
        var token = cancellation.Token;
        isSearchRunning = true;
        SetSearchUiState(true);
        strictCandidates.Clear();
        searchedRevision = null;
        foreach (var row in rows)
        {
            row.Candidates.Clear();
            row.CandidateCount = 0;
            row.SelectedCandidate = null;
            row.Status = RelinkStatus.Missing;
        }
        try
        {
            SetSummaryText("許可したフォルダーの素材を元の名前とSHA-256で照合しています。");
            //CSJ,variable=history,履歴
            var history = await resolveMetadata(token);
            var expected = new StrictRelinkAssets(context.YmmpPath, context.OriginalJsonText, history.Selected.Manifest);
            var jobs = new Dictionary<string, Task<IReadOnlyList<AssetPoolMatch>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (expected.FindExpected(row.OriginalPath) is not { } metadata)
                {
                    row.Message = "期待SHA-256が未登録";
                    continue;
                }
                //CSJ,variable=name,名前
                var name = metadata.ExternalRelink?.OriginalFileName ?? AssetPool.GetOriginalFileName(row.OriginalPath);
                //CSJ,variable=key,キー
                var key = metadata.Sha256 + ":" + name;
                if (!jobs.TryGetValue(key, out var job))
                {
                    job = Task.Run(() => AssetPool.FindAsync(name, metadata.Sha256, [root.FolderName], manifestPath, token), token);
                    jobs.Add(key, job);
                }
                //CSJ,variable=matches,一致項目
                var matches = await job;
                //CSJ,variable=candidates,候補一覧
                var candidates = matches.Select(match => new StrictMatch(name, metadata.Sha256, root.FolderName,
                    metadata.ExternalRelink is not null, match)).ToArray();
                strictCandidates.Add(row.RowIndex, candidates);
                foreach (var candidate in candidates) row.Candidates.Add(candidate.Candidate.CandidatePath);
                row.CandidateCount = candidates.Length;
                row.Status = candidates.Length switch { 0 => RelinkStatus.NotFound, 1 => RelinkStatus.Updated, _ => RelinkStatus.Ambiguous };
                row.SelectedCandidate = candidates.Length == 1 ? candidates[0].Candidate.CandidatePath : null;
                row.Message = candidates.Length switch
                {
                    0 => "名前とSHA-256が一致する候補なし",
                    1 => $"SHA-256 {metadata.Sha256}",
                    _ => "名前とSHA-256が一致する候補が複数あります。再リンク先を選択してください。",
                };
            }
            searchedRevision = history.Selected.Receipt;
            SetSummaryText($"{rows.Count(row => row.CandidateCount > 0)}件に一致候補があります。再リンク先を確認してから別名保存してください。");
        }
        catch (OperationCanceledException) { SetSummaryText("探索を中止しました。保存するには再度照合してください。"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException)
        {
            logger.Error(ex, "Strict relink inspection failed.");
            SetSummaryText("照合を完了できません。検索先、素材プールの記録、プロジェクトのメタデータを確認してください。");
        }
        finally
        {
            searchCts = null;
            isSearchRunning = false;
            SetSearchUiState(false);
        }
    }

    //CSJ,method=SaveStrictCandidatesAsync,厳格候補一覧を保存する（非同期）
    private async Task SaveStrictCandidatesAsync(RelinkDocumentContext snapshot, CancellationToken token)
    {
        if (resolveMetadata is null || searchedRevision is null || metadataDirectory is null)
            throw new InvalidDataException("素材を保存する前に、関連付けたメタデータを使って照合してください。");
        //CSJ,variable=association,関連付け
        var association = await resolveMetadata(token);
        if (searchedRevision != association.Selected.Receipt || !association.Graph.Heads.Contains(searchedRevision))
            throw new InvalidDataException("選択した履歴が変わりました。素材を検索し直してください。");
        //CSJ,variable=selection,選択
        var selection = snapshot.Rows.Where(row => row.Status == RelinkStatus.Updated && row.SelectedCandidate is not null).ToArray();
        if (selection.Length is 0 or > 10)
            throw new InvalidDataException("操作は候補1件以上10件以下に分けてください。");
        //CSJ,variable=verified,検証済み
        var verified = new List<AssetPoolVerification>();
        var proofs = new List<AssetRelinkRecovery.StrictAssetProof>();
        var approvedRoots = new HashSet<string>(StringComparer.Ordinal);
        var hasExternalMetadata = false;
        try
        {
            foreach (var row in selection)
            {
                if (!strictCandidates.TryGetValue(row.RowIndex, out var candidates) ||
                    candidates.SingleOrDefault(item => item.Candidate.CandidatePath == row.SelectedCandidate) is not { } choice)
                    throw new InvalidDataException("使用する候補を再度検索してください。");
                verified.Add(await Task.Run(() => AssetPool.OpenVerifiedAsync(choice.Candidate,
                    choice.ExpectedName, choice.ExpectedSha256, token), token));
                proofs.Add(new AssetRelinkRecovery.StrictAssetProof(row.RowIndex, choice.ExpectedName,
                    choice.Candidate.ManifestPath, choice.Candidate.ManifestSha256));
                approvedRoots.Add(choice.ApprovedRoot);
                hasExternalMetadata |= choice.HasExternalMetadata;
            }
            //CSJ,variable=projectDirectory,プロジェクトディレクトリ
            var projectDirectory = Path.GetDirectoryName(snapshot.YmmpPath)!;
            if (!hasExternalMetadata && verified.All(asset => StrictRelinkAssets.IsDescendant(projectDirectory, asset.CandidatePath)))
                await SaveManagedRelinkAsync(snapshot, association, proofs.ToArray(), token);
            else
                await SaveStrictExternalOutputAsync(snapshot, association, proofs.ToArray(), approvedRoots.ToArray(), token);
        }
        finally
        {
            //CSJ,variable=asset,素材
            foreach (var asset in verified) await asset.DisposeAsync();
        }
    }

    //CSJ,method=SaveStrictExternalOutputAsync,厳格外部出力を保存する（非同期）
    private async Task SaveStrictExternalOutputAsync(RelinkDocumentContext snapshot,
        AssetRevisionSeries.BranchSelection reviewed, AssetRelinkRecovery.StrictAssetProof[] proofs,
        IReadOnlyList<string> approvedRoots, CancellationToken token)
    {
        if (RegisterDerivedAsync is null)
            throw new InvalidDataException("一覧への派生登録を開始できません。登録済みの元プロジェクトから再度開いてください。");
        const string notice = "結果は別名のPFとして保存し、独立した派生IDで一覧に登録します。元PF・登録・メタデータ履歴と素材は維持されます。";
        var rowsToSave = snapshot.Rows.Where(row => row.Status == RelinkStatus.Updated && row.SelectedCandidate is not null);
        //CSJ,variable=details,詳細
        var details = string.Join("\n\n", rowsToSave.Select(row => $"{row.OriginalPath}\n→ {row.SelectedCandidate}"));
        if (MessageBox.Show($"{notice}\n\n{details}\n\n続けますか？", "厳格な素材再リンク",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var output = new SaveFileDialog { Filter = "YMM Project (*.ymmp)|*.ymmp", OverwritePrompt = false,
            Title = "元のプロジェクトと同じフォルダーに別名で保存", InitialDirectory = Path.GetDirectoryName(snapshot.YmmpPath) };
        if (output.ShowDialog() != true) return;
        if (!Path.GetExtension(output.FileName).Equals(".ymmp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("出力には .ymmp を指定してください。");
        await using var source = ProjectFileEvidence.OpenRead(snapshot.YmmpPath);
        await VerifyManagedIdentityAsync(source, token);
        var current = await resolveMetadata!(token);
        if (reviewed.Selected.Receipt != current.Selected.Receipt ||
            !AssetRevisionSeries.SameTips(reviewed.Graph.Heads, current.Graph.Heads))
            throw new InvalidDataException("メタデータ履歴が変わりました。素材を検索し直してください。");
        //CSJ,variable=evidence,証跡
        var evidence = await AssetRelinkRecovery.PrepareExternalOutputAsync(snapshot, output.FileName, metadataDirectory!,
            reviewed.Graph.Root, reviewed.Selected.Receipt, reviewed.Graph.Heads, proofs, approvedRoots, token);
        try
        {
            var (staged, error) = await AssetRelinkRecovery.StageExternalOutputAsync(evidence, snapshot, saveService, approvedRoots, token);
            if (!staged)
            {
                SetSummaryText($"出力の準備を確認できません。{error}\n回復記録 {evidence}");
                return;
            }
            await CompleteExternalRelinkAsync(evidence, reviewed.Graph.Root, snapshot.YmmpPath, approvedRoots, token);
        }
        catch (DerivedRegistrationException ex) { ShowPendingDerivedRegistration(evidence, ex); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetSummaryText($"処理を中止しました。出力と一覧の状況は「再リンクの回復」で確認できます。\n{evidence}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            logger.Error(ex, "Derived relink did not finish.");
            SetSummaryText($"出力または一覧登録の完了を確認できません。「再リンクの回復」と一覧の状態を確認してください。\n{evidence}");
        }
    }

    //CSJ,method=CompleteExternalRelinkAsync,外部再リンクを完了する（非同期）
    private async Task CompleteExternalRelinkAsync(string evidence, AssetRevisionSeries.Head sourceRoot, string source,
        IReadOnlyList<string> approvedRoots, CancellationToken token)
    {
        if (RegisterDerivedAsync is not { } register)
            throw new InvalidDataException("元プロジェクトの一覧登録を確認できません。");
        //CSJ,variable=receipt,照合記録
        var receipt = await AssetRelinkRecovery.CompleteExternalOutputAsync(evidence, metadataDirectory!, sourceRoot, source,
            approvedRoots, token, async (item, heldToken) =>
            {
                try { await register(item, heldToken); }
                catch (Exception ex) { throw new DerivedRegistrationException(ex); }
            });
        SetSummaryText($"別名PFと履歴を確認し、派生プロジェクトを登録しました。\n{receipt.Output.Output}\n派生ID {receipt.Output.Revision.ProjectId}\n回復記録 {evidence}");
    }

    //CSJ,method=TryRecoverExternalRelinkAsync,外部再リンクの復旧を試みる（非同期）
    private async Task<bool> TryRecoverExternalRelinkAsync(string evidence, string source, CancellationToken token)
    {
        //CSJ,variable=review,レビュー
        var review = await AssetRelinkRecovery.InspectExternalRecoveryAsync(evidence, source, token);
        if (review is null) return false;
        if (review.State == AssetRelinkRecovery.ExternalRecoveryState.NotStaged)
        {
            SetSummaryText($"作業ファイルの作成前に中断されました。この記録は保持しました。最初から再リンクしてください。\n{evidence}");
            return true;
        }
        if (review.State == AssetRelinkRecovery.ExternalRecoveryState.Unknown)
        {
            SetSummaryText($"出力を確認する証拠が不足しています。出力や記録は保持しました。\n{evidence}");
            return true;
        }
        if (RegisterDerivedAsync is null)
            throw new InvalidDataException("元プロジェクトの一覧登録を確認できません。");
        if (MessageBox.Show($"出力 {review.Output}\n派生ID {review.DerivedProjectId}\n\n素材の検索先を再指定して派生登録を回復しますか？",
                "外部素材の再リンク回復", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return true;
        //CSJ,variable=approved,承認済み
        var approved = new List<string>();
        //CSJ,variable=root,ルート
        foreach (var root in review.ApprovedRoots)
        {
            token.ThrowIfCancellationRequested();
            // A saved network path is displayed as text. The picker only opens the location selected by the user.
            var picker = new OpenFolderDialog { Title = $"今回も許可するフォルダーを指定してください — {root}" };
            if (picker.ShowDialog() != true) return true;
            var chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picker.FolderName));
            if (!string.Equals(chosen, root, StringComparison.Ordinal))
            {
                SetSummaryText("回復記録と同じ検索先を指定してください。外部素材を確認する前に停止しました。");
                return true;
            }
            approved.Add(chosen);
        }
        //CSJ,variable=association,関連付け
        var association = await resolveMetadata!(token);
        await CompleteExternalRelinkAsync(evidence, association.Graph.Root, source, approved, token);
        return true;
    }

    private void ShowPendingDerivedRegistration(string evidence, DerivedRegistrationException ex)
    {
        logger.Error(ex, "Derived output and history verified; list registration did not return a known success.");
        SetSummaryText($"別名PFと派生履歴は確認済みです。一覧登録の完了を確認できません。「再リンクの回復」で確認してください。\n{evidence}");
    }
}
