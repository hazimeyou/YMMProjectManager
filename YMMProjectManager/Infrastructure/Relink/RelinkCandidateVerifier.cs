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
}
