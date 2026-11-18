using System.IO;
using System.Security.Cryptography;

namespace YMMProjectManager.Infrastructure.Relink;

internal static class RelinkCandidateVerifier
{
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

    public static async Task<IReadOnlyList<string>> HashSelectedAsync(IEnumerable<RelinkRow> rows, CancellationToken token)
    {
        var selected = rows.Where(row => row.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(row.SelectedCandidate)).ToArray();
        if (selected.Length > 10) throw new InvalidDataException("候補をすべて確認できるよう、10件以下に分けてください。");
        var result = new List<string>(selected.Length);
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            var candidate = row.SelectedCandidate!;
            await using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            result.Add($"{candidate}\nSHA-256: {Convert.ToHexString(hash)}");
        }
        return result;
    }

    public static async Task<IReadOnlyList<string>> CompareExpectedAsync(IEnumerable<RelinkRow> rows, string projectPath, Guid projectId, AssetManifest manifest, CancellationToken token)
    {
        if (projectId == Guid.Empty || manifest.ProjectId != projectId) throw new InvalidDataException("素材メタデータのプロジェクト ID が一致しません。");
        var root = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? throw new InvalidDataException("プロジェクトフォルダーがありません。");
        var selected = rows.Where(row => row.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(row.SelectedCandidate)).ToArray();
        if (selected.Length > 10) throw new InvalidDataException("候補は10件以下に分けてください。");
        var results = new List<string>();
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            var original = Path.GetFullPath(row.OriginalPath, root);
            var asset = manifest.Assets.SingleOrDefault(item => string.Equals(Path.GetFullPath(item.RelativePath, root), original, StringComparison.OrdinalIgnoreCase));
            await using var stream = new FileStream(row.SelectedCandidate!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            var status = asset is null ? "期待ハッシュなし" : string.Equals(asset.Sha256, actual, StringComparison.OrdinalIgnoreCase) ? "一致" : "不一致";
            results.Add($"{row.SelectedCandidate}\n期待 SHA-256: {asset?.Sha256 ?? "なし"}\n候補 SHA-256: {actual}\n判定: {status}");
        }
        return results;
    }
}
