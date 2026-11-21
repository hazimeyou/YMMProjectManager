using System.IO;
using System.Windows;
using Microsoft.Win32;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Relink;

namespace YMMProjectManager.Presentation.Relink;

public partial class RelinkMainWindow
{
    private sealed record StrictMatch(string ExpectedName, string ExpectedSha256, AssetPoolMatch Candidate);
    private readonly Dictionary<int, StrictMatch[]> strictCandidates = [];
    private AssetRevisionSeries.Head? searchedRevision;

    private async Task SearchStrictAsync()
    {
        if (context is null || rows.Count == 0) return;
        if (resolveMetadata is null)
        {
            SetSummaryText("先にこのプロジェクトの素材メタデータを関連付けてください。期待SHA-256がない参照は変更できません。");
            return;
        }
        var mode = MessageBox.Show("配布名を元の名前に照合する素材プールの記録を使用しますか？\n「いいえ」で元の名前の素材をフォルダーから探します。",
            "素材の照合", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (mode == MessageBoxResult.Cancel) return;
        var root = new OpenFolderDialog { Title = "検索を許可するフォルダーを選択" };
        if (root.ShowDialog() != true) return;
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
                var name = AssetPool.GetOriginalFileName(row.OriginalPath);
                var key = metadata.Sha256 + ":" + name;
                if (!jobs.TryGetValue(key, out var job))
                {
                    job = Task.Run(() => AssetPool.FindAsync(name, metadata.Sha256, [root.FolderName], manifestPath, token), token);
                    jobs.Add(key, job);
                }
                var matches = await job;
                var candidates = matches.Select(match => new StrictMatch(name, metadata.Sha256, match)).ToArray();
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

    private async Task SaveStrictCandidatesAsync(RelinkDocumentContext snapshot, CancellationToken token)
    {
        if (resolveMetadata is null || searchedRevision is null || metadataDirectory is null)
            throw new InvalidDataException("素材を保存する前に、関連付けたメタデータを使って照合してください。");
        var association = await resolveMetadata(token);
        if (searchedRevision != association.Selected.Receipt || !association.Graph.Heads.Contains(searchedRevision))
            throw new InvalidDataException("選択した履歴が変わりました。素材を検索し直してください。");
        var selection = snapshot.Rows.Where(row => row.Status == RelinkStatus.Updated && row.SelectedCandidate is not null).ToArray();
        if (selection.Length is 0 or > 10)
            throw new InvalidDataException("操作は候補1件以上10件以下に分けてください。");
        var verified = new List<AssetPoolVerification>();
        var proofs = new List<AssetRelinkRecovery.StrictAssetProof>();
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
            }
            var projectDirectory = Path.GetDirectoryName(snapshot.YmmpPath)!;
            if (verified.All(asset => StrictRelinkAssets.IsDescendant(projectDirectory, asset.CandidatePath)))
                await SaveManagedRelinkAsync(snapshot, association, proofs.ToArray(), token);
            else
                await SaveStrictExternalOutputAsync(snapshot, association, token);
        }
        finally
        {
            foreach (var asset in verified) await asset.DisposeAsync();
        }
    }

    private async Task SaveStrictExternalOutputAsync(RelinkDocumentContext snapshot,
        AssetRevisionSeries.BranchSelection reviewed, CancellationToken token)
    {
        const string notice = "プロジェクト外の素材を使うため、結果を別名のPFに保存します。素材はコピーしません。元のメタデータ履歴と関連付けは保持され、出力には引き継ぎません。";
        var rowsToSave = snapshot.Rows.Where(row => row.Status == RelinkStatus.Updated && row.SelectedCandidate is not null);
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
        var (saved, error) = await saveService.SaveAsAsync(snapshot, output.FileName, token);
        SetSummaryText(saved ? $"別名で保存しました。元の関連付けは保持されます。\n{output.FileName}" : error ?? "別名保存を確認できません。");
    }
}
