using System.IO;
using System.Text.Json;

namespace YMMProjectManager.Infrastructure.Relink;

// Utf8JsonReader supplies token boundaries; only selected value tokens are replaced.
//CSJ,class=RelinkJsonUpdate,再リンクJSON更新
internal static class RelinkJsonUpdate
{
    //CSJ,record=Target,対象
    private readonly record struct Target(int Start, int Length, string Path, bool DirectItem);

    //CSJ,method=Apply,適用する
    public static byte[] Apply(byte[] source, IReadOnlyList<RelinkRow> rows)
    {
        var bom = source.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        //CSJ,variable=reader,読み取り元
        var reader = new Utf8JsonReader(source.AsSpan(bom), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        if (!reader.Read()) throw new InvalidDataException("JSON が空です。");
        var targets = new List<Target>();
        Collect(ref reader, false, false, false, targets, bom);
        if (reader.Read()) throw new InvalidDataException("JSON の構造が不正です。");
        var seen = new HashSet<int>();
        var replacements = new List<(Target Target, byte[] Bytes)>();
        foreach (var row in rows)
        {
            if (!seen.Add(row.RowIndex) || row.RowIndex < 0 || row.RowIndex >= targets.Count ||
                !string.Equals(row.OriginalPath, targets[row.RowIndex].Path, StringComparison.Ordinal) || !targets[row.RowIndex].DirectItem)
                throw new InvalidDataException("再リンク対象を特定できません。再読み込みしてください。");
            EnsureSupported(row);
            replacements.Add((targets[row.RowIndex], JsonSerializer.SerializeToUtf8Bytes(row.SelectedCandidate!)));
        }
        using var output = new MemoryStream();
        var cursor = 0;
        foreach (var (target, bytes) in replacements.OrderBy(x => x.Target.Start))
        {
            output.Write(source, cursor, target.Start - cursor);
            output.Write(bytes);
            cursor = target.Start + target.Length;
        }
        output.Write(source, cursor, source.Length - cursor);
        return output.ToArray();
    }

    internal static void EnsureSupported(RelinkRow row)
    {
        var selected = row.SelectedCandidate!;
        if (Path.GetExtension(row.OriginalPath).Equals(".psd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(selected).Equals(".psd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PSD のレイヤー参照は未検証のため更新できません。");
        if (row.TypeHint.Contains("VideoItem", StringComparison.OrdinalIgnoreCase) &&
            (Path.GetExtension(row.OriginalPath).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(selected).Equals(".png", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("PNG 連番の再リンクは未検証のため更新できません。");
    }

    //CSJ,method=Collect,収集する
    private static void Collect(ref Utf8JsonReader reader, bool insideItems, bool directItem, bool itemsArray, List<Target> targets, int bom)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            //CSJ,variable=names,名前一覧
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new InvalidDataException("JSON プロパティが不正です。");
                //CSJ,variable=name,名前
                var name = reader.GetString()!;
                if (!names.Add(name)) throw new InvalidDataException("重複した JSON プロパティがあるため安全に更新できません。");
                if (!reader.Read()) throw new InvalidDataException("JSON 値が不正です。");
                if (insideItems && name == "FilePath" && reader.TokenType == JsonTokenType.String)
                {
                    //CSJ,variable=path,パス
                    var path = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                        targets.Add(new Target(checked((int)reader.TokenStartIndex + bom), checked((int)(reader.BytesConsumed - reader.TokenStartIndex)), path, directItem));
                }
                else Collect(ref reader, insideItems || name == "Items", false, name == "Items", targets, bom);
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw new InvalidDataException("JSON オブジェクトが不正です。");
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) Collect(ref reader, insideItems, itemsArray && reader.TokenType == JsonTokenType.StartObject, false, targets, bom);
            if (reader.TokenType != JsonTokenType.EndArray) throw new InvalidDataException("JSON 配列が不正です。");
        }
    }
}
