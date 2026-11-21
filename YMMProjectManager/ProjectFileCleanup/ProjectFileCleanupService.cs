using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure;

internal enum ProjectFileCleanupDisposition
{
    SafeToRemove,
    Conditional,
}

internal sealed record ProjectFileCleanupCandidate(
    string PropertyName, ProjectFileCleanupDisposition Disposition, string Reason);

internal sealed record ProjectFileCleanupReport(
    string SourcePath, ProjectFileIdentity SourceIdentity, IReadOnlyList<ProjectFileCleanupCandidate> Candidates)
{
    internal bool CanSave => Candidates.Any(candidate => candidate.Disposition == ProjectFileCleanupDisposition.SafeToRemove);

    internal string FormatForDisplay()
    {
        if (Candidates.Count == 0)
            return "PFのルートに整理候補はありません。";
        var rows = Candidates.Select(candidate =>
            $"{candidate.PropertyName}　{(candidate.Disposition == ProjectFileCleanupDisposition.SafeToRemove ? "削除可能" : "保持")}　{candidate.Reason}");
        return string.Join(Environment.NewLine, rows) + Environment.NewLine + Environment.NewLine +
            (CanSave ? "削除可能な項目だけを除いたコピーを元のフォルダーに保存できます。" :
                "このPFは確認された条件を満たす削除候補がありません。");
    }
}

internal sealed class ProjectFileCleanupService(Action? beforePublish = null)
{
    private const string VerifiedModelBuild = "4.57.0.1+f7f1e4a691d0b476c5693586b4be2ce0c9b13a3c";
    private const int MaxProjectBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    internal Task<ProjectFileCleanupReport> InspectAsync(string sourcePath, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            var source = GetProjectPath(sourcePath);
            using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(source)!, enforceShare: true);
            await using var stream = OpenProject(source);
            var bytes = await ReadAsync(stream, token).ConfigureAwait(false);
            return new ProjectFileCleanupReport(source, ReadIdentity(stream, bytes), Inspect(bytes, CurrentHostBuild(), token));
        }, token);

    internal Task<string> SaveAsAsync(ProjectFileCleanupReport preview, string outputPath, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(preview);
            var source = GetProjectPath(preview.SourcePath);
            var destination = GetProjectPath(outputPath);
            var directory = Path.GetDirectoryName(source)!;
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(directory, Path.GetDirectoryName(destination), StringComparison.Ordinal))
                throw new IOException("元PFと同じフォルダーの新しい名前を指定してください。");

            using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
            await using var stream = OpenProject(source);
            var bytes = await ReadAsync(stream, token).ConfigureAwait(false);
            if (ReadIdentity(stream, bytes) != preview.SourceIdentity)
                throw new IOException("確認後に元PFが変更されました。もう一度候補を確認してください。");

            var fields = Inspect(bytes, CurrentHostBuild(), token)
                .Where(candidate => candidate.Disposition == ProjectFileCleanupDisposition.SafeToRemove)
                .Select(candidate => candidate.PropertyName)
                .ToHashSet(StringComparer.Ordinal);
            var approved = preview.Candidates.Where(candidate => candidate.Disposition == ProjectFileCleanupDisposition.SafeToRemove)
                .Select(candidate => candidate.PropertyName);
            if (fields.Count == 0 || !fields.SetEquals(approved))
                throw new InvalidOperationException("確認済みの削除候補がありません。");

            var output = RemoveProperties(bytes, fields, token);
            if (Inspect(output, CurrentHostBuild(), token).Any(candidate => fields.Contains(candidate.PropertyName)))
                throw new InvalidDataException("整理後のPFに削除予定の項目が残っています。");
            token.ThrowIfCancellationRequested();

            var temporary = Path.Combine(directory, $".ymmpm-pf-{Guid.NewGuid():N}.tmp");
            await using var staged = OwnedFilePublication.CreateOwnedOutputFile(temporary, readBack: true);
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

    private static string CurrentHostBuild() => typeof(YukkuriMovieMaker.Project.Project).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

    private static string GetProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("PFの完全なファイルパスを指定してください。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            fullPath.AsSpan(2).Contains(':'))
            throw new ArgumentException(".ymmp ファイルを指定してください。", nameof(path));
        return fullPath;
    }

    private static FileStream OpenProject(string source) =>
        new(OwnedFilePublication.OpenOrdinaryPath(source, expectDirectory: false, allowRename: false), FileAccess.Read);

    private static async Task<byte[]> ReadAsync(FileStream stream, CancellationToken token)
    {
        var length = stream.Length;
        if (length is <= 0 or > MaxProjectBytes)
            throw new InvalidDataException("PFが空か、64 MiBを超えています。");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static ProjectFileIdentity ReadIdentity(FileStream stream, byte[] bytes)
    {
        var (volume, id) = ProjectFileEvidence.ReadPhysicalIdentity(stream);
        return new ProjectFileIdentity(volume, id, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal static IReadOnlyList<ProjectFileCleanupCandidate> Inspect(byte[] bytes, string hostBuild, CancellationToken token)
    {
        var offset = bytes.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        token.ThrowIfCancellationRequested();
        try { StrictUtf8.GetCharCount(bytes.AsSpan(offset)); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("UTF-8のPFを指定してください。", ex); }
        using var json = JsonDocument.Parse(bytes.AsMemory(offset));
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

        var isKnownModel = hostBuild == VerifiedModelBuild && hasModernTimelines && !hasLegacyTimeline &&
            !root.EnumerateObject().Any(property => property.Name.StartsWith('$'));
        List<ProjectFileCleanupCandidate> candidates = [];
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

        void Add(JsonProperty property, bool redundant, string removableReason, string preserveReason)
        {
            var safe = isKnownModel && redundant;
            candidates.Add(new ProjectFileCleanupCandidate(property.Name,
                safe ? ProjectFileCleanupDisposition.SafeToRemove : ProjectFileCleanupDisposition.Conditional,
                safe ? removableReason : redundant ? "このYMM4ビルドまたはPF形式では省略時の動作を確認していません。" : preserveReason));
        }

        static bool IsTimeline(JsonElement item) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Items", out var entries) &&
            entries.ValueKind == JsonValueKind.Array;
    }

    private static void ValidateProperties(JsonElement element, StringComparer comparer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(comparer);
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

    private static byte[] RemoveProperties(byte[] bytes, HashSet<string> removed, CancellationToken token)
    {
        var offset = bytes.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(offset));
        reader.Read();
        var properties = new List<(int Start, int End, bool Remove)>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            token.ThrowIfCancellationRequested();
            var start = checked(offset + (int)reader.TokenStartIndex);
            var remove = removed.Contains(reader.GetString()!);
            reader.Read();
            reader.Skip();
            properties.Add((start, checked(offset + (int)reader.BytesConsumed), remove));
        }

        using var output = new MemoryStream(bytes.Length);
        var cursor = 0;
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
