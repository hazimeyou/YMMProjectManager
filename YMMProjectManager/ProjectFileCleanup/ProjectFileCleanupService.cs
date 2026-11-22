using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure;

//CSJ,enum=ProjectFileCleanupDisposition,プロジェクトファイル後片付け処置区分
internal enum ProjectFileCleanupDisposition
{
    SafeToRemove,
    Conditional,
}

//CSJ,record=ProjectFileCleanupCandidate,プロジェクトファイル後片付け候補
internal sealed record ProjectFileCleanupCandidate(
    string PropertyName, ProjectFileCleanupDisposition Disposition, string Reason);

//CSJ,record=ProjectFileCleanupReport,プロジェクトファイル後片付け報告
internal sealed record ProjectFileCleanupReport(
    string SourcePath, ProjectFileIdentity SourceIdentity, IReadOnlyList<ProjectFileCleanupCandidate> Candidates)
{
    //CSJ,property=CanSave,保存の可否
    internal bool CanSave => Candidates.Any(candidate => candidate.Disposition == ProjectFileCleanupDisposition.SafeToRemove);

    internal string FormatForDisplay()
    {
        if (Candidates.Count == 0)
            return "PFのルートに整理候補はありません。";
        //CSJ,variable=rows,行一覧
        var rows = Candidates.Select(candidate =>
            $"{candidate.PropertyName}　{(candidate.Disposition == ProjectFileCleanupDisposition.SafeToRemove ? "削除可能" : "保持")}　{candidate.Reason}");
        return string.Join(Environment.NewLine, rows) + Environment.NewLine + Environment.NewLine +
            (CanSave ? "削除可能な項目だけを除いたコピーを元のフォルダーに保存できます。" :
                "このPFは確認された条件を満たす削除候補がありません。");
    }
}

//CSJ,class=ProjectFileCleanupService,プロジェクトファイル後片付けサービス
internal sealed class ProjectFileCleanupService(Action? beforePublish = null)
{
    //CSJ,field=VerifiedModelBuild,検証済みモデル構築
    private const string VerifiedModelBuild = "4.57.0.1+f7f1e4a691d0b476c5693586b4be2ce0c9b13a3c";
    //CSJ,field=MaxProjectBytes,最大プロジェクトバイト列
    private const int MaxProjectBytes = 64 * 1024 * 1024;
    //CSJ,field=StrictUtf8,厳格UTF-8
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    //CSJ,method=InspectAsync,点検する（非同期）
    internal Task<ProjectFileCleanupReport> InspectAsync(string sourcePath, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            var source = GetProjectPath(sourcePath);
            //CSJ,variable=fence,整合性境界
            using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(source)!, enforceShare: true);
            //CSJ,variable=stream,ストリーム
            await using var stream = OpenProject(source);
            //CSJ,variable=bytes,バイト列
            var bytes = await ReadAsync(stream, token).ConfigureAwait(false);
            return new ProjectFileCleanupReport(source, ReadIdentity(stream, bytes), Inspect(bytes, CurrentHostBuild(), token));
        }, token);

    //CSJ,method=SaveAsAsync,別名で保存する（非同期）
    internal Task<string> SaveAsAsync(ProjectFileCleanupReport preview, string outputPath, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(preview);
            var source = GetProjectPath(preview.SourcePath);
            //CSJ,variable=destination,格納先
            var destination = GetProjectPath(outputPath);
            //CSJ,variable=directory,ディレクトリ
            var directory = Path.GetDirectoryName(source)!;
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(directory, Path.GetDirectoryName(destination), StringComparison.Ordinal))
                throw new IOException("元PFと同じフォルダーの新しい名前を指定してください。");

            //CSJ,variable=fence,整合性境界
            using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
            //CSJ,variable=stream,ストリーム
            await using var stream = OpenProject(source);
            //CSJ,variable=bytes,バイト列
            var bytes = await ReadAsync(stream, token).ConfigureAwait(false);
            if (ReadIdentity(stream, bytes) != preview.SourceIdentity)
                throw new IOException("確認後に元PFが変更されました。もう一度候補を確認してください。");

            //CSJ,variable=fields,フィールド一覧
            var fields = Inspect(bytes, CurrentHostBuild(), token)
                .Where(candidate => candidate.Disposition == ProjectFileCleanupDisposition.SafeToRemove)
                .Select(candidate => candidate.PropertyName)
                .ToHashSet(StringComparer.Ordinal);
            //CSJ,variable=approved,承認済み
            var approved = preview.Candidates.Where(candidate => candidate.Disposition == ProjectFileCleanupDisposition.SafeToRemove)
                .Select(candidate => candidate.PropertyName);
            if (fields.Count == 0 || !fields.SetEquals(approved))
                throw new InvalidOperationException("確認済みの削除候補がありません。");

            var output = RemoveProperties(bytes, fields, token);
            if (Inspect(output, CurrentHostBuild(), token).Any(candidate => fields.Contains(candidate.PropertyName)))
                throw new InvalidDataException("整理後のPFに削除予定の項目が残っています。");
            token.ThrowIfCancellationRequested();

            //CSJ,variable=temporary,一時
            var temporary = Path.Combine(directory, $".ymmpm-pf-{Guid.NewGuid():N}.tmp");
            await using var staged = OwnedFilePublication.CreateOwnedOutputFile(temporary, readBack: true);
            //CSJ,variable=published,確定済み
            var published = false;
            try
            {
                await staged.WriteAsync(output, token).ConfigureAwait(false);
                await staged.FlushAsync(token).ConfigureAwait(false);
                staged.Flush(flushToDisk: true);
                staged.Position = 0;
                var observed = await SHA256.HashDataAsync(staged, token).ConfigureAwait(false);
                if (!SHA256.HashData(output).AsSpan().SequenceEqual(observed))
                    throw new IOException("整理済みPFの書き込みを確認できません。");
                beforePublish?.Invoke();
                token.ThrowIfCancellationRequested();
                OwnedFilePublication.RenameOpenedPath(staged.SafeFileHandle, destination);
                published = true;
                return destination;
            }
            finally
            {
                if (!published) OwnedFilePublication.TryDeleteOpenedPath(staged.SafeFileHandle);
            }
        }, token);

    //CSJ,method=CurrentHostBuild,現在のホストのビルド番号を取得する
    private static string CurrentHostBuild() => typeof(YukkuriMovieMaker.Project.Project).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

    //CSJ,method=GetProjectPath,プロジェクトのパスを取得する
    private static string GetProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("PFの完全なファイルパスを指定してください。", nameof(path));
        //CSJ,variable=fullPath,完全パス
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            fullPath.AsSpan(2).Contains(':'))
            throw new ArgumentException(".ymmp ファイルを指定してください。", nameof(path));
        return fullPath;
    }

    //CSJ,method=OpenProject,プロジェクトを開く
    private static FileStream OpenProject(string source) =>
        new(OwnedFilePublication.OpenOrdinaryPath(source, expectDirectory: false, allowRename: false), FileAccess.Read);

    //CSJ,method=ReadAsync,読み取る（非同期）
    private static async Task<byte[]> ReadAsync(FileStream stream, CancellationToken token)
    {
        //CSJ,variable=length,長さ
        var length = stream.Length;
        if (length is <= 0 or > MaxProjectBytes)
            throw new InvalidDataException("PFが空か、64 MiBを超えています。");
        //CSJ,variable=bytes,バイト列
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    //CSJ,method=ReadIdentity,識別情報を読み取る
    private static ProjectFileIdentity ReadIdentity(FileStream stream, byte[] bytes)
    {
        var (volume, id) = ProjectFileEvidence.ReadPhysicalIdentity(stream);
        return new ProjectFileIdentity(volume, id, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    //CSJ,method=Inspect,点検する
    internal static IReadOnlyList<ProjectFileCleanupCandidate> Inspect(byte[] bytes, string hostBuild, CancellationToken token)
    {
        //CSJ,variable=offset,オフセット
        var offset = bytes.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        token.ThrowIfCancellationRequested();
        try { StrictUtf8.GetCharCount(bytes.AsSpan(offset)); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("UTF-8のPFを指定してください。", ex); }
        using var json = JsonDocument.Parse(bytes.AsMemory(offset));
        //CSJ,variable=root,ルート
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("PFのルートがJSONオブジェクトではありません。");
        ValidateProperties(root, StringComparer.OrdinalIgnoreCase, token);

        var hasModernTimelines = root.TryGetProperty("Timelines", out var timelines) &&
            timelines.ValueKind == JsonValueKind.Array && timelines.GetArrayLength() > 0 &&
            timelines.EnumerateArray().All(IsTimeline);
        var hasLegacyTimeline = root.TryGetProperty("Timeline", out var timeline);
        if (!hasModernTimelines && !(hasLegacyTimeline && IsTimeline(timeline)))
            throw new InvalidDataException("PFのタイムライン構造を確認できません。");

        //CSJ,variable=isKnownModel,既知のモデルかどうか
        var isKnownModel = hostBuild == VerifiedModelBuild && hasModernTimelines && !hasLegacyTimeline &&
            !root.EnumerateObject().Any(property => property.Name.StartsWith('$'));
        //CSJ,variable=candidates,候補一覧
        List<ProjectFileCleanupCandidate> candidates = [];
        //CSJ,variable=property,プロパティ
        foreach (var property in root.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            switch (property.Name)
            {
                case "LayoutXml":
                    Add(property, property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == string.Empty,
                        "空文字は確認済みのYMM4モデルで省略時と同じ既定値です。",
                        "保存されたレイアウトは画面復元に使われるため保持します。");
                    break;
                case "ToolStates":
                    Add(property, property.Value.ValueKind == JsonValueKind.Null,
                        "nullは確認済みのYMM4モデルで省略時と同じ既定値です。",
                        "ツールやプラグインの状態を含むため、空オブジェクトも含めて保持します。");
                    break;
            }
        }
        return candidates.AsReadOnly();

        //CSJ,method=Add,追加する
        void Add(JsonProperty property, bool redundant, string removableReason, string preserveReason)
        {
            //CSJ,variable=safe,安全
            var safe = isKnownModel && redundant;
            candidates.Add(new ProjectFileCleanupCandidate(property.Name,
                safe ? ProjectFileCleanupDisposition.SafeToRemove : ProjectFileCleanupDisposition.Conditional,
                safe ? removableReason : redundant ? "このYMM4ビルドまたはPF形式では省略時の動作を確認していません。" : preserveReason));
        }

        //CSJ,method=IsTimeline,タイムラインかを判定する
        static bool IsTimeline(JsonElement item) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Items", out var entries) &&
            entries.ValueKind == JsonValueKind.Array;
    }

    //CSJ,method=ValidateProperties,プロパティ一覧を検証する
    private static void ValidateProperties(JsonElement element, StringComparer comparer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            //CSJ,variable=names,名前一覧
            var names = new HashSet<string>(comparer);
            //CSJ,variable=property,プロパティ
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("PFに重複するJSON項目名があります。");
                ValidateProperties(property.Value, StringComparer.Ordinal, token);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateProperties(item, StringComparer.Ordinal, token);
        }
    }

    //CSJ,method=RemoveProperties,プロパティ一覧を除去する
    private static byte[] RemoveProperties(byte[] bytes, HashSet<string> removed, CancellationToken token)
    {
        //CSJ,variable=offset,オフセット
        var offset = bytes.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        //CSJ,variable=reader,読み取り元
        var reader = new Utf8JsonReader(bytes.AsSpan(offset));
        reader.Read();
        //CSJ,variable=properties,プロパティ一覧
        var properties = new List<(int Start, int End, bool Remove)>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            token.ThrowIfCancellationRequested();
            var start = checked(offset + (int)reader.TokenStartIndex);
            //CSJ,variable=remove,除去
            var remove = removed.Contains(reader.GetString()!);
            reader.Read();
            reader.Skip();
            properties.Add((start, checked(offset + (int)reader.BytesConsumed), remove));
        }

        using var output = new MemoryStream(bytes.Length);
        var cursor = 0;
        //CSJ,variable=index,インデックス
        for (var index = 0; index < properties.Count; index++)
        {
            if (!properties[index].Remove) continue;
            var first = index;
            while (index + 1 < properties.Count && properties[index + 1].Remove) index++;
            // Exclude a following comma for an interior group or the preceding comma at the end.
            var atEnd = index + 1 == properties.Count;
            var start = atEnd && first > 0 ? properties[first - 1].End : properties[first].Start;
            var end = atEnd ? properties[index].End : properties[index + 1].Start;
            output.Write(bytes.AsSpan(cursor, start - cursor));
            cursor = end;
        }
        output.Write(bytes.AsSpan(cursor));
        return output.ToArray();
    }
}
