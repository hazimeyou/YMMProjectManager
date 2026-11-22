using System.IO;
using System.Security.Cryptography;

namespace YMMProjectManager.Infrastructure.Relink;

//CSJ,class=RelinkCandidateVerifier,再リンク候補の検証処理
internal static class RelinkCandidateVerifier
{
    //CSJ,class=CandidateReadLocks,候補の読み取りロック
    internal sealed class CandidateReadLocks : IDisposable
    {
        //CSJ,field=files,ファイル一覧
        private readonly List<FileStream> files = [];

        internal CandidateReadLocks(IEnumerable<RelinkRow> rows)
        {
            try
            {
                foreach (var row in rows.Where(row => row.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(row.SelectedCandidate)))
                    files.Add(new FileStream(row.SelectedCandidate!, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        //CSJ,method=Dispose,破棄する
        public void Dispose()
        {
            //CSJ,variable=file,ファイル
            foreach (var file in files) file.Dispose();
        }
    }

    //CSJ,method=SnapshotRows,スナップショット行一覧
    public static List<RelinkRow> SnapshotRows(IEnumerable<RelinkRow> rows) => rows.Select(row => new RelinkRow
    {
        RowIndex = row.RowIndex,
        TypeHint = row.TypeHint,
        OriginalPath = row.OriginalPath,
        FileName = row.FileName,
        Extension = row.Extension,
        Status = row.Status,
        SelectedCandidate = row.SelectedCandidate,
    }).ToList();

    //CSJ,method=HashSelectedAsync,ハッシュ選択中の（非同期）
    public static async Task<IReadOnlyList<string>> HashSelectedAsync(IEnumerable<RelinkRow> rows, CancellationToken token)
    {
        var selected = rows.Where(row => row.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(row.SelectedCandidate)).ToArray();
        if (selected.Length > 10) throw new InvalidDataException("候補をすべて確認できるよう、10件以下に分けてください。");
        var result = new List<string>(selected.Length);
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            var candidate = row.SelectedCandidate!;
            //CSJ,variable=stream,ストリーム
            await using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            //CSJ,variable=hash,ハッシュ
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            result.Add($"{candidate}\nSHA-256: {Convert.ToHexString(hash)}");
        }
        return result;
    }

    //CSJ,method=CompareExpectedAsync,期待値を比較する（非同期）
    public static async Task<IReadOnlyList<string>> CompareExpectedAsync(IEnumerable<RelinkRow> rows, string projectPath, Guid projectId, AssetManifest manifest, CancellationToken token)
    {
        if (projectId == Guid.Empty || manifest.ProjectId != projectId) throw new InvalidDataException("素材メタデータのプロジェクト ID が一致しません。");
        //CSJ,variable=root,ルート
        var root = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? throw new InvalidDataException("プロジェクトフォルダーがありません。");
        var selected = rows.Where(row => row.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(row.SelectedCandidate)).ToArray();
        if (selected.Length > 10) throw new InvalidDataException("候補は10件以下に分けてください。");
        //CSJ,variable=results,結果一覧
        var results = new List<string>();
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            var original = Path.GetFullPath(row.OriginalPath, root);
            //CSJ,variable=asset,素材
            var asset = manifest.Assets.SingleOrDefault(item => string.Equals(Path.GetFullPath(item.RelativePath, root), original, StringComparison.OrdinalIgnoreCase));
            //CSJ,variable=stream,ストリーム
            await using var stream = new FileStream(row.SelectedCandidate!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            //CSJ,variable=status,状態
            var status = asset is null ? "期待ハッシュなし" : string.Equals(asset.Sha256, actual, StringComparison.OrdinalIgnoreCase) ? "一致" : "不一致";
            results.Add($"{row.SelectedCandidate}\n期待 SHA-256: {asset?.Sha256 ?? "なし"}\n候補 SHA-256: {actual}\n判定: {status}");
        }
        return results;
    }
}
