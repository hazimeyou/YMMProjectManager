using System.IO;
using System.Text.Json;
using System.Security.Cryptography;

namespace YMMProjectManager.Infrastructure.Packaging;

internal sealed record YmmpxExclusionRule(string Path, bool IsFolder, bool IsExcluded);

internal static class YmmpxExclusionRules
{
    private const int MaxRuleBytes = 1024 * 1024;
    private const int MaxRules = 1000;
    internal const string GlobalFileName = "pm-ymmpx-exclude.json";

    internal static string GetLocalName(string projectPath) =>
        Path.GetFileNameWithoutExtension(projectPath) + ".ymmpm-exclude.json";

    internal sealed record Snapshot(IReadOnlyList<YmmpxExclusionRule> Rules, (ulong, Guid)? Identity, string? Sha256);

    internal static async Task<Snapshot> LoadAsync(string path, CancellationToken token, bool missingIsEmpty = false)
    {
        try
        {
            using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
            await using var stream = YmmpxTransfer.OpenOrdinaryRead(path);
            if (stream.Length is <= 0 or > MaxRuleBytes)
                throw new InvalidDataException("YMMPX 除外設定のサイズが不正です。");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var payload = bytes.AsMemory();
            if (payload.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) payload = payload[3..];
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > MaxRules)
                throw new InvalidDataException("YMMPX 除外設定の形式が不正です。");
            var rules = new List<YmmpxExclusionRule>();
            foreach (var row in json.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (row.ValueKind != JsonValueKind.Object) throw InvalidRule();
                string? input = null;
                bool? folder = null;
                bool? excluded = null;
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in row.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw InvalidRule();
                    switch (property.Name)
                    {
                        case "Path" or "FilePath" when input is null && property.Value.ValueKind == JsonValueKind.String:
                            input = property.Value.GetString();
                            break;
                        case "IsFolder" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                            folder = property.Value.GetBoolean();
                            break;
                        case "IsExcluded" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                            excluded = property.Value.GetBoolean();
                            break;
                        case "KindText" when property.Value.ValueKind == JsonValueKind.String:
                            break;
                        default:
                            throw InvalidRule();
                    }
                }
                var normalized = Normalize(input);
                if (normalized is null)
                    throw InvalidRule();
                rules.Add(new(normalized, folder ?? false, excluded ?? true));
            }
            return new(Merge([], rules), ProjectFileEvidence.ReadPhysicalIdentity(stream), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (FileNotFoundException) when (missingIsEmpty) { return Missing(); }
        catch (IOException ex) when (missingIsEmpty && IsFileNotFound(ex)) { return Missing(); }
        catch (JsonException ex) { throw new InvalidDataException("破損した YMMPX 除外設定があります。同梱を停止しました。", ex); }

        Snapshot Missing()
        {
            EnsureNoInterruptedUpdate(path);
            return new([], null, null);
        }
    }

    private static void EnsureNoInterruptedUpdate(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var pattern = Path.GetFileName(path) + ".previous-*";
        if (Directory.EnumerateFileSystemEntries(parent, pattern).Any())
            throw new InvalidDataException("中断された YMMPX 除外設定の更新があります。元の設定を確認してください。");
    }

    internal static IReadOnlyList<YmmpxExclusionRule> Merge(IEnumerable<YmmpxExclusionRule> global,
        IEnumerable<YmmpxExclusionRule> local)
    {
        var comparers = new[] { new Dictionary<string, YmmpxExclusionRule>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, YmmpxExclusionRule>(StringComparer.OrdinalIgnoreCase) };
        foreach (var rule in global.Concat(local))
            comparers[rule.IsFolder ? 1 : 0][rule.Path] = rule;
        return comparers[0].Values.Concat(comparers[1].Values).ToArray();
    }

    internal static bool IsExcluded(string rawPath, string projectDirectory, IReadOnlyList<YmmpxExclusionRule> rules)
    {
        var candidates = PathCandidates(rawPath, projectDirectory);
        foreach (var rule in rules)
        {
            if (!rule.IsExcluded) continue;
            foreach (var target in PathCandidates(rule.Path, projectDirectory))
                if (candidates.Any(candidate => Matches(candidate, target, rule.IsFolder))) return true;
        }
        return false;
    }

    internal static async Task<IDisposable> PinReviewedAsync(
        IReadOnlyList<(string Path, Snapshot Reviewed)> expected, CancellationToken token)
    {
        var guard = new ReviewLease();
        try
        {
            foreach (var (path, reviewed) in expected)
            {
                token.ThrowIfCancellationRequested();
                guard.Handles.Add(OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true));
                if (reviewed.Identity is null)
                {
                    // Claim the exact canonical name. A competing create or no-replace rename
                    // either already owns it or must wait for this publication to finish.
                    try { guard.Handles.Add(OwnedFilePublication.CreateOwnedOutputFile(path, deleteOnClose: true)); }
                    catch (IOException ex)
                    {
                        throw new InvalidDataException("除外設定が作成されたか、不在を保護できません。同梱対象を確認してください。", ex);
                    }
                    // An interrupted import may leave the canonical name vacant. Its
                    // previous settings must be noticed after the reservation is acquired.
                    EnsureNoInterruptedUpdate(path);
                    continue;
                }
                var file = YmmpxTransfer.OpenOrdinaryRead(path);
                guard.Handles.Add(file);
                if (file.Length is <= 0 or > MaxRuleBytes ||
                    ProjectFileEvidence.ReadPhysicalIdentity(file) != reviewed.Identity ||
                    Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)) != reviewed.Sha256)
                    throw new InvalidDataException("除外設定が変更されました。同梱対象をもう一度確認してください。");
            }

            token.ThrowIfCancellationRequested();
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    private sealed class ReviewLease : IDisposable
    {
        internal List<IDisposable> Handles { get; } = [];
        public void Dispose()
        {
            for (var index = Handles.Count - 1; index >= 0; index--)
                Handles[index].Dispose();
        }
    }

    internal static async Task ImportAsync(string destination, Snapshot reviewed, IReadOnlyList<YmmpxExclusionRule> rules,
        CancellationToken token)
    {
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(destination)!, enforceShare: true);
        var operation = Guid.NewGuid().ToString("N");
        var tempPath = destination + ".tmp-" + operation;
        var backup = destination + ".previous-" + operation;
        await using var file = OwnedFilePublication.CreateOwnedOutputFile(tempPath);
        bool published = false;
        try
        {
            await JsonSerializer.SerializeAsync(file, rules, cancellationToken: token).ConfigureAwait(false);
            if (file.Length > MaxRuleBytes || rules.Count > MaxRules)
                throw new InvalidDataException("反映先の除外設定がサイズ上限を超えています。");
            file.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            if (reviewed.Identity is null)
            {
                // A second import or a newly created external file wins the destination.
                if ((await LoadAsync(destination, token, missingIsEmpty: true).ConfigureAwait(false)).Identity is not null)
                    throw new IOException("確認後に対象の除外設定が作成されました。");
                OwnedFilePublication.RenameOpenedPath(file.SafeFileHandle, destination);
                published = true;
                return;
            }

            using var handle = OwnedFilePublication.OpenOrdinaryPath(destination, expectDirectory: false, allowRename: true);
            await using var current = new FileStream(handle, FileAccess.Read);
            if (ProjectFileEvidence.ReadPhysicalIdentity(current) != reviewed.Identity ||
                current.Length is <= 0 or > MaxRuleBytes ||
                Convert.ToHexString(await SHA256.HashDataAsync(current, token).ConfigureAwait(false)) != reviewed.Sha256)
                throw new IOException("確認後に対象の除外設定が変更されました。");
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(handle, backup);
            try
            {
                OwnedFilePublication.RenameOpenedPath(file.SafeFileHandle, destination);
                published = true;
            }
            catch (IOException)
            {
                // Restore to a vacant name only; if another writer got there first, preserve both.
                try { OwnedFilePublication.RenameOpenedPath(handle, destination); }
                catch (IOException) { }
                throw;
            }
        }
        finally { if (!published) OwnedFilePublication.TryDeleteOpenedPath(file.SafeFileHandle); }
    }

    private static bool IsFileNotFound(IOException error) =>
        error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 };

    private static InvalidDataException InvalidRule() =>
        new("YMMPX 除外ルールを解釈できません。同梱を停止しました。");

    private static string[] PathCandidates(string path, string directory)
    {
        var raw = Normalize(path);
        if (raw is null) return [];
        try
        {
            var absolute = Path.GetFullPath(raw, directory);
            return Normalize(absolute) is { } full ? [raw, full] : [raw];
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [raw];
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) return null;
        var value = path.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) return null;
            value = uri.LocalPath;
        }
        if (value.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0) return null;
        value = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(value);
        var trimmed = value.TrimEnd(Path.DirectorySeparatorChar);
        return root is not null && trimmed.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? root
            : trimmed;
    }

    private static bool Matches(string candidate, string rule, bool isFolder) =>
        candidate.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
        (isFolder && candidate.StartsWith(Path.EndsInDirectorySeparator(rule) ? rule : rule + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
}
