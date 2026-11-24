using System.Buffers;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitRepository,Gitリポジトリ操作
public sealed partial class GitRepository
{
    private const int PublicationListingLimit = 4 * 1024 * 1024;
    //CSJ,field=PublicationObjectLimit,Git送信検査のオブジェクト上限
    private const int PublicationObjectLimit = 4 * 1024 * 1024;
    //CSJ,field=PublicationTotalLimit,Git送信検査の総量上限
    private const long PublicationTotalLimit = 128L * 1024 * 1024;
    //CSJ,field=StrictPublicationUtf8,Git送信検査用の厳格なUTF-8
    private static readonly UTF8Encoding StrictPublicationUtf8 = new(false, true);
    //CSJ,field=publicationAssetNames,Git送信検査で使う素材名の一覧
    private static readonly Regex KnownCredentialText = new(
        @"(?:\b(?:gh[pousr]_[A-Za-z0-9_]{30,}|github_pat_[A-Za-z0-9_]{30,})\b|-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----|https?://[^/\s:@]+:[^/\s@]+@)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly HashSet<string> publicationAssetNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> publicationAssetHashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds media known to the selected project before inspecting staged or outgoing objects.</summary>
    //CSJ,method=SetPublicationAssets,Git送信検査で使う素材一覧を設定する
    internal void SetPublicationAssets(IEnumerable<string> names, IEnumerable<string> hashes)
    {
        //CSJ,variable=name,名前
        foreach (var name in names)
            publicationAssetNames.Add(Path.GetFileName(PackagingDetector.NormalizeFilePathKey(name)));
        //CSJ,variable=hash,ハッシュ
        foreach (var hash in hashes)
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
                publicationAssetHashes.Add(hash);
    }

    //CSJ,method=InspectCurrentCommitAsync,現在のコミットを点検する（非同期）
    internal async Task<GitResult> InspectCurrentCommitAsync(CancellationToken token)
    {
        if (!HasDirectRepository()) return UnsafeRemote("Select a direct Git repository.");
        var (tip, error) = await ReadBranchTipAsync(trusted: true, token, pullSettings: []).ConfigureAwait(false);
        return error ?? new GitResult(0, tip!.ObjectId, "");
    }

    private static GitResult PublicationRefusal(string reason) => new(1, "", reason, GitFailure.MediaPublication);

    //CSJ,method=CheckStagePublicationFileAsync,ステージ対象ファイルの送信可否を確認する（非同期）
    private async Task<GitResult?> CheckStagePublicationFileAsync(string path, FileStream file, CancellationToken token)
    {
        if (IsBlockedPublicationPath(path) || file.Length > PublicationObjectLimit)
            return PublicationRefusal("The selected file is an asset, private record or exceeds the media inspection limit.");
        //CSJ,variable=bytes,バイト列
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        //CSJ,variable=reason,理由
        var reason = InspectPublicationBlob(bytes, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase));
        return reason is null ? null : PublicationRefusal(reason);
    }

    //CSJ,method=HasOrdinaryPublicationObjectStore,検査対象のGitオブジェクト保管先が通常形式か判定する
    private bool HasOrdinaryPublicationObjectStore()
    {
        var git = Path.Combine(directory, ".git");
        var objects = Path.Combine(git, "objects");
        if (!HasDirectRepository() || !CanUsePullMetadataFile(git, "objects/info/alternates") ||
            HasGitEntry(Path.Combine(objects, "info", "alternates")) ||
            HasGitEntry(Path.Combine(objects, "info", "http-alternates")) ||
            HasGitEntry(Path.Combine(git, "shallow")) ||
            HasGitEntry(Path.Combine(git, "info", "grafts")))
            return false;
        try
        {
            if (Directory.EnumerateFiles(objects).Any()) return false;
            //CSJ,variable=folder,フォルダー
            foreach (var folder in Directory.EnumerateDirectories(objects))
            {
                if (!IsOrdinaryDirectory(folder) || Directory.EnumerateDirectories(folder).Any()) return false;
                if (Directory.EnumerateFiles(folder).Any(path =>
                    !IsRegularFile(path) || path.EndsWith(".promisor", StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // A Push can introduce old objects even when the tip no longer contains the media.
    // Inspect all reachable history before opening a network connection.
    //CSJ,method=CheckPushPublicationAsync,プッシュ予定の内容の送信可否を確認する（非同期）
    private async Task<GitResult?> CheckPushPublicationAsync(string commit, CancellationToken token)
    {
        //CSJ,variable=knownFailure,既知の失敗
        var knownFailure = await ReadKnownPublicationAssetsAsync(token).ConfigureAwait(false);
        if (knownFailure is not null) return knownFailure;
        //CSJ,variable=complete,完了
        var complete = await InspectPublicationGitAsync(["rev-parse", "--is-shallow-repository"], token).ConfigureAwait(false);
        if (!complete.Success || complete.StandardOutput.Trim() != "false")
            return PublicationRefusal("Complete local Git history is required to check outgoing media.");
        //CSJ,variable=paths,パス一覧
        var paths = await InspectPublicationGitAsync(
            ["log", "--full-history", "--root", "--diff-merges=separate", "--format=", "--raw", "-z",
                "--no-show-signature", "--no-notes", "--no-decorate", "--no-renames", "--no-ext-diff",
                "--no-textconv", "--diff-filter=AMT", commit, "--"], token).ConfigureAwait(false);
        var objects = await InspectPublicationGitAsync(["rev-list", "--objects", "--no-object-names", commit, "--"], token).ConfigureAwait(false);
        if (!paths.Success || !objects.Success)
            return PublicationRefusal("Cannot verify all historical paths and objects.");
        //CSJ,variable=entries,項目一覧
        var entries = paths.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        //CSJ,variable=names,名前一覧
        var names = new List<string>();
        if (entries.Length % 2 != 0) return PublicationRefusal("Cannot identify historical file names.");
        for (var i = 0; i < entries.Length; i += 2)
        {
            //CSJ,variable=fields,フィールド一覧
            var fields = entries[i].TrimStart('\n').Split(' ');
            if (fields.Length != 5 || !fields[0].StartsWith(':') || fields[1] is not ("100644" or "100755"))
                return PublicationRefusal("Git history contains a link or unsupported Git entry.");
            //CSJ,variable=path,パス
            var path = entries[i + 1];
            if (IsBlockedPublicationPath(path))
                return PublicationRefusal("Git history contains an asset, package or private record path.");
            names.Add(path);
        }
        //CSJ,variable=ids,ID一覧
        var ids = objects.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return await CheckPublicationObjectsAsync(ids, names, token).ConfigureAwait(false);
    }

    // A commit is checked against the immutable tree the caller will publish.
    //CSJ,method=CheckPublicationTreeAsync,送信予定のGitツリーを確認する（非同期）
    private async Task<GitResult?> CheckPublicationTreeAsync(string tree, CancellationToken token)
    {
        //CSJ,variable=knownFailure,既知の失敗
        var knownFailure = await ReadKnownPublicationAssetsAsync(token).ConfigureAwait(false);
        if (knownFailure is not null) return knownFailure;
        var listing = await InspectPublicationGitAsync(["ls-tree", "-r", "-z", tree, "--"], token).ConfigureAwait(false);
        if (!listing.Success) return PublicationRefusal("Cannot verify the staged tree.");
        //CSJ,variable=ids,ID一覧
        var ids = new HashSet<string>(StringComparer.Ordinal);
        //CSJ,variable=names,名前一覧
        var names = new List<string>();
        //CSJ,variable=record,記録
        foreach (var record in listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            //CSJ,variable=separator,区切り
            var separator = record.IndexOf('\t');
            if (separator < 0) return PublicationRefusal("Cannot identify a staged entry.");
            //CSJ,variable=fields,フィールド一覧
            var fields = record[..separator].Split(' ');
            if (fields.Length != 3 || fields[0] is not ("100644" or "100755") || fields[1] != "blob" || !IsObjectId(fields[2]))
                return PublicationRefusal("The staged tree contains a link or unsupported Git entry.");
            //CSJ,variable=path,パス
            var path = record[(separator + 1)..];
            if (IsBlockedPublicationPath(path))
                return PublicationRefusal("The staged tree contains an asset, package or private record path.");
            names.Add(path);
            ids.Add(fields[2]);
        }
        return await CheckPublicationObjectsAsync(ids, names, token).ConfigureAwait(false);
    }

    //CSJ,method=IsBlockedPublicationPath,Git送信禁止パスかを判定する
    private bool IsBlockedPublicationPath(string path) =>
        !IsOrdinaryPublicationPath(path) ||
        path.EndsWith(".ymmpx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ymme", StringComparison.OrdinalIgnoreCase) ||
        publicationAssetNames.Contains(Path.GetFileName(path)) ||
        IsPrivateApplicationPath(path) ||
        IsTransferAssetReference(path) ||
        IsAssetPoolPayload(path);

    private static bool IsOrdinaryPublicationPath(string path) =>
        !HasHiddenText(path) && path.Split('/').All(component => GitPullWindowsTree.IsOrdinaryWindowsName(component));

    private static bool IsPrivateApplicationPath(string path) =>
        path.Split('/').Any(component =>
            component.Equals(".ymmpm-lineage.json", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("ymmpm-transfer.json", StringComparison.OrdinalIgnoreCase) ||
            HasOperationName(component, ".ymmpm-transfer-") ||
            HasOperationName(component, ".ymmpm-package-") ||
            HasOperationName(component, ".ymmpm-package-input-") ||
            HasOperationName(component, ".ymmpx-") ||
            HasOperationName(component, ".ymmpm-relink-") ||
            HasOperationName(component, ".ymmpm-relink-external-") ||
            HasOperationName(component, ".ymmpm-relink.tmp-") ||
            HasOperationName(component, ".ymmpm-derived-") ||
            HasOperationName(component, ".ymmpm-derived-pending-") ||
            HasOperationName(component, ".ymmpm-legacy-export-") ||
            HasOperationName(component, ".ymmpm-comparison-") ||
            HasOperationName(component, "projects.json.parked-") ||
            HasOperationName(component, "projects.json.tmp-") ||
            HasOperationName(component, "projects.json.publication-") ||
            HasOperationName(component, "YMMProjectManagerSettings.json.previous-") ||
            HasOperationName(component, "YMMProjectManagerSettings.json.recovery-") ||
            component.Equals("YMMProjectManagerSettings.json", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("YMMProjectManager.log", StringComparison.OrdinalIgnoreCase) ||
            component.StartsWith("YMMProjectManager.log.", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("pm-ymmpx-exclude.json", StringComparison.OrdinalIgnoreCase) ||
            component.Contains(".ymmpm-exclude.json", StringComparison.OrdinalIgnoreCase) ||
            component.StartsWith("pm-ymmpx-exclude.json.", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("projects.json.recovery", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("projects.json.publication-pending", StringComparison.OrdinalIgnoreCase) ||
            component.Equals(".git-credentials", StringComparison.OrdinalIgnoreCase) ||
            component.Equals(".netrc", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("_netrc", StringComparison.OrdinalIgnoreCase) ||
            component.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            component.Equals(".env.local", StringComparison.OrdinalIgnoreCase) ||
            IsGenerationRestorePathComponent(component));

    private static bool IsGenerationRestorePathComponent(string component)
    {
        const int operationLength = 32;
        const string candidateSuffix = ".restore.tmp";
        const string fallbackBackupSuffix = "_before-restore";
        const string journalPrefix = ".ymm-restore-";
        const string journalSuffix = ".pending.json";
        const int keyLength = 64;

        if (HasOperationName(component, ".ymm-restore-recovery-"))
            return true;
        var name = component.AsSpan();
        if (name.Length == operationLength + candidateSuffix.Length && name.EndsWith(candidateSuffix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name[..operationLength], "N", out _))
            return true;

        var extension = name.IndexOf('.');
        var stem = extension < 0 ? name : name[..extension];
        if (stem.EndsWith(fallbackBackupSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var keyBeforeRestore = stem[..^fallbackBackupSuffix.Length];
            if (Guid.TryParseExact(keyBeforeRestore, "N", out _) ||
                DateTime.TryParseExact(keyBeforeRestore, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                (keyBeforeRestore.Length == 48 && keyBeforeRestore[15] == '_' &&
                 DateTime.TryParseExact(keyBeforeRestore[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
                 Guid.TryParseExact(keyBeforeRestore[16..], "N", out _)))
                return true;
        }

        if (name.Length < journalPrefix.Length + keyLength + journalSuffix.Length ||
            !name.StartsWith(journalPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var key = name.Slice(journalPrefix.Length, keyLength);
        var suffix = name[(journalPrefix.Length + keyLength)..];
        if (key.IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0 || !suffix.StartsWith(journalSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var tail = suffix[journalSuffix.Length..];
        return tail.IsEmpty ||
            (tail.Length == operationLength + ".tmp".Length + 1 && tail[0] == '.' &&
                tail.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(tail.Slice(1, operationLength), "N", out _));
    }

    //CSJ,method=HasOperationName,操作名が含まれているか判定する
    private static bool HasOperationName(string component, string prefix) =>
        component.Length >= prefix.Length + 32 &&
        component.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(component.AsSpan(prefix.Length, 32), "N", out _);

    //CSJ,method=IsAssetPoolPayload,素材プール本体データかを判定する
    private static bool IsAssetPoolPayload(string path)
    {
        var components = path.Split('/');
        for (var i = 0; i < components.Length - 1; i++)
        {
            //CSJ,variable=component,構成要素
            var component = components[i];
            if (component.StartsWith(".asset-pool-pending-", StringComparison.OrdinalIgnoreCase)) return true;
            if (component.Length == 87 && component.StartsWith("asset-", StringComparison.OrdinalIgnoreCase) && component[22] == '-' &&
                component.AsSpan(6, 16).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 &&
                component.AsSpan(23).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
                return i != components.Length - 2 || !components[^1].Equals(AssetPool.ManifestFileName, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    //CSJ,method=InspectPublicationGitAsync,送信検査のためにGitの情報を取得する（非同期）
    private Task<GitResult> InspectPublicationGitAsync(string[] arguments, CancellationToken token, string? input = null,
        //CSJ,parameter=rawOutput,生データ出力
        Func<Stream, CancellationToken, Task>? rawOutput = null) =>
        RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}", .. arguments],
            token, trustedProcess: true, ignoreLegacyGrafts: true, pullSettings: [], standardInput: input,
            outputLimit: PublicationListingLimit, rawOutput: rawOutput, noLazyFetch: true);

    private async Task<GitResult?> CheckPublicationObjectsAsync(IReadOnlyCollection<string> ids,
        //CSJ,parameter=paths,パス一覧
        IReadOnlyCollection<string> paths, CancellationToken token)
    {
        if (ids.Count == 0) return null;
        if (ids.Count > 40_000 || ids.Any(id => !IsObjectId(id)))
            return PublicationRefusal("The Git object list cannot be checked within the inspection bounds.");
        //CSJ,variable=info,情報
        var info = await InspectPublicationGitAsync(["cat-file", "--batch-check"], token,
            string.Join("\n", ids) + "\n").ConfigureAwait(false);
        if (!info.Success) return PublicationRefusal("Cannot read local Git objects.");
        //CSJ,variable=records,記録一覧
        var records = info.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (records.Length != ids.Count) return PublicationRefusal("The Git object list is incomplete.");

        var blobs = new List<(string Id, int Size)>();
        //CSJ,variable=total,合計
        long total = 0;
        foreach (var (entry, expected) in records.Zip(ids))
        {
            //CSJ,variable=fields,フィールド一覧
            var fields = entry.Split(' ');
            if (fields.Length != 3 || fields[0] != expected || fields[1] is not ("commit" or "tree" or "blob") ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                return PublicationRefusal("The Git object list contains an unreadable entry.");
            if (fields[1] != "blob") continue;
            total += size;
            if (size > PublicationObjectLimit || total > PublicationTotalLimit)
                return PublicationRefusal("Git contains files too large to verify for media publication.");
            blobs.Add((expected, size));
        }
        if (blobs.Count == 0) return null;

        string? refusal = null;
        var historicalAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var historicalAssetHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blobHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=buffer,バッファー
        var buffer = ArrayPool<byte>.Shared.Rent(PublicationObjectLimit + 1);
        try
        {
            var scanned = await InspectPublicationGitAsync(["cat-file", "--batch"], token,
                string.Join("\n", blobs.Select(blob => blob.Id)) + "\n", async (stream, cancellation) =>
                {
                    foreach (var blob in blobs)
                    {
                        var header = Encoding.ASCII.GetBytes($"{blob.Id} blob {blob.Size.ToString(CultureInfo.InvariantCulture)}\n");
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, header.Length), cancellation).ConfigureAwait(false);
                        if (!header.AsSpan().SequenceEqual(buffer.AsSpan(0, header.Length)))
                            throw new InvalidDataException("Git object identity changed during inspection.");
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, blob.Size + 1), cancellation).ConfigureAwait(false);
                        if (buffer[blob.Size] != (byte)'\n') throw new InvalidDataException("Git object boundary is invalid.");
                        refusal ??= InspectPublicationBlob(buffer.AsSpan(0, blob.Size), historicalAssetNames, historicalAssetHashes, blobHashes);
                    }
                    if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellation).ConfigureAwait(false) != 0)
                        throw new InvalidDataException("Unexpected Git object output.");
                }).ConfigureAwait(false);
            if (!scanned.Success) return PublicationRefusal("Cannot verify all Git object contents.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return PublicationRefusal("Git object contents could not be read safely.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        if (refusal is not null) return PublicationRefusal(refusal);
        if (historicalAssetHashes.Overlaps(blobHashes))
            return PublicationRefusal("Git contains a blob matching an asset recorded in its metadata history.");
        return paths.Any(path => historicalAssetNames.Contains(Path.GetFileName(path)))
            ? PublicationRefusal("Git contains material referenced by a project in the inspected history.")
            : null;
    }

    private string? InspectPublicationBlob(ReadOnlySpan<byte> bytes, HashSet<string> historicalAssetNames,
        HashSet<string> historicalAssetHashes, HashSet<string> blobHashes)
    {
        //CSJ,variable=hash,ハッシュ
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        blobHashes.Add(hash);
        if (publicationAssetHashes.Contains(hash))
            return "Git contains a blob matching a registered asset SHA-256.";
        //CSJ,variable=content,内容
        string content;
        try { content = StrictPublicationUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return "Git contains binary or unverified encoded content."; }
        var start = content.AsSpan().TrimStart().TrimStart('\uFEFF').TrimStart();
        if (content.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ||
            start.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
            return "Git contains binary content or an external large file pointer.";
        if (KnownCredentialText.IsMatch(content))
            return "Git contains a recognizable credential or private key.";
        if (start.StartsWith("WEBVTT", StringComparison.Ordinal) &&
            (start.Length == 6 || char.IsWhiteSpace(start[6])))
            return "Git contains a WebVTT media asset.";

        // Git may canonicalize text to LF. Compare exact bytes and uniform line-ending
        // variants so a known caption does not escape inspection after normalization.
        if (content.Contains('\n'))
        {
            var lf = content.Replace("\r\n", "\n", StringComparison.Ordinal);
            var lfHash = Convert.ToHexString(SHA256.HashData(StrictPublicationUtf8.GetBytes(lf)));
            var crlfHash = Convert.ToHexString(SHA256.HashData(StrictPublicationUtf8.GetBytes(lf.Replace("\n", "\r\n", StringComparison.Ordinal))));
            blobHashes.Add(lfHash);
            blobHashes.Add(crlfHash);
            if (publicationAssetHashes.Contains(lfHash) || publicationAssetHashes.Contains(crlfHash))
                return "Git contains a blob matching a registered asset after line-ending normalization.";
        }

        if (start.StartsWith("["))
        {
            try
            {
                using var document = JsonDocument.Parse(start.ToString());
                if (document.RootElement.EnumerateArray().Any(IsPrivateExclusionRule))
                    return "Git contains a local path in a YMMPX exclusion rule.";
            }
            catch (JsonException) { /* Ordinary text may contain a JSON fragment. */ }
        }
        if (start.StartsWith("{"))
        {
            try
            {
                //CSJ,variable=document,文書
                using var document = JsonDocument.Parse(content.TrimStart('\uFEFF'));
                //CSJ,variable=root,ルート
                var root = document.RootElement;
                if (IsPrivateProjectListRecord(root))
                    return "Git contains a local project list or recovery record.";
                if (IsPrivateSettingsRecord(root))
                    return "Git contains local settings or recovery data.";
                if (IsPrivateRelinkRecord(root))
                    return "Git contains a local relink recovery or publication record.";
                if (IsPrivateGenerationRestoreRecord(root) ||
                    (root.TryGetProperty("GenerationId", out _) && root.TryGetProperty("SourceProjectPath", out _) &&
                     root.TryGetProperty("FileSize", out _) && root.TryGetProperty("Sha256", out _)))
                    return "Git contains a local generation snapshot record or restore evidence.";
                if (IsTransferManifest(root))
                    return "Git contains a local YMMPX asset-transfer manifest.";
                if (ReadProjectAssetNames(root, historicalAssetNames))
                    return "Git contains a project prepared for local YMMPX asset transfer.";
                if (root.TryGetProperty("Assets", out var assets) && assets.ValueKind == JsonValueKind.Array &&
                    root.TryGetProperty("ProjectId", out _))
                    //CSJ,variable=asset,素材
                    foreach (var asset in assets.EnumerateArray())
                        ReadAsset(asset, "RelativePath", historicalAssetNames, historicalAssetHashes);
                if (root.TryGetProperty("DistributionFileName", out _))
                {
                    if (root.TryGetProperty("OriginalFileName", out _) &&
                        root.TryGetProperty("OriginalReference", out var reference) &&
                        reference.ValueKind == JsonValueKind.String && reference.GetString() is { } original &&
                        IsMachineLocalReference(original))
                        return "Git contains a pool record that exposes a non-portable original path.";
                    ReadAsset(root, "OriginalFileName", historicalAssetNames, historicalAssetHashes);
                    ReadAsset(root, "DistributionFileName", historicalAssetNames, historicalAssetHashes);
                }
            }
            catch (JsonException) { /* Ordinary text may contain a JSON fragment. */ }
        }
        return null;
    }

    private static bool IsPrivateProjectListRecord(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("Projects", out var projects) && projects.ValueKind == JsonValueKind.Array &&
            root.TryGetProperty("Folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
            return true;
        return root.TryGetProperty("Payload", out var payload) && payload.ValueKind == JsonValueKind.String &&
            (root.TryGetProperty("Sha256", out var hash) || root.TryGetProperty("Hash", out hash)) &&
            hash.ValueKind == JsonValueKind.String && hash.GetString() is { Length: 64 } digest &&
            digest.All(Uri.IsHexDigit);
    }

    private static bool IsPrivateSettingsRecord(JsonElement root) =>
        (root.TryGetProperty("SearchFolders", out var folders) && folders.ValueKind == JsonValueKind.Array &&
         folders.EnumerateArray().Any(folder => folder.ValueKind == JsonValueKind.String &&
             IsMachineLocalReference(folder.GetString()!))) ||
        (root.TryGetProperty("Version", out _) && root.TryGetProperty("OperationId", out _) &&
         root.TryGetProperty("TargetName", out _) && root.TryGetProperty("DirectoryId", out _) &&
         root.TryGetProperty("FileId", out _) && root.TryGetProperty("Sha256", out _) &&
         root.TryGetProperty("StagedId", out _) && root.TryGetProperty("StagedSha256", out _));

    private static bool IsPrivateRelinkRecord(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("Version", out _) &&
        root.TryGetProperty("OperationId", out _) &&
        root.TryGetProperty("OutputSha256", out _) &&
        (root.TryGetProperty("Identity", out _) ||
            (root.TryGetProperty("SourceSha256", out _) && root.TryGetProperty("Approved", out _)));

    private static bool IsPrivateGenerationRestoreRecord(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("Version", out _) &&
        root.TryGetProperty("Operation", out var operation) && operation.ValueKind == JsonValueKind.String && operation.TryGetGuid(out _) &&
        root.TryGetProperty("TargetPath", out _) &&
        root.TryGetProperty("RetainedPath", out _) &&
        root.TryGetProperty("CandidatePath", out _) &&
        root.TryGetProperty("Original", out var original) && IsRestoreFileVersion(original) &&
        root.TryGetProperty("Intended", out var intended) && IsRestoreFileVersion(intended);

    private static bool IsRestoreFileVersion(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("Volume", out _) &&
        value.TryGetProperty("FileId", out var identity) && identity.ValueKind == JsonValueKind.String && identity.TryGetGuid(out _) &&
        value.TryGetProperty("Sha256", out var hash) && hash.ValueKind == JsonValueKind.String &&
        hash.GetString() is { Length: 64 } digest && digest.All(Uri.IsHexDigit);

    //CSJ,method=IsTransferManifest,受け渡し目録かを判定する
    private static bool IsTransferManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        //CSJ,variable=properties,プロパティ一覧
        var properties = root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return properties.Contains("Version") && properties.Contains("PackageName") &&
            properties.Contains("PackageSha256") && properties.Contains("Assets");
    }

    //CSJ,method=IsMachineLocalReference,端末ローカル参照かを判定する
    private static bool IsPrivateExclusionRule(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object &&
        (HasBoolean(entry, "IsFolder") || HasBoolean(entry, "IsExcluded")) &&
        (entry.TryGetProperty("Path", out var path) || entry.TryGetProperty("FilePath", out path)) &&
        path.ValueKind == JsonValueKind.String && IsMachineLocalReference(path.GetString()!);

    private static bool HasBoolean(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsMachineLocalReference(string reference)
    {
        //CSJ,variable=path,パス
        var path = reference.Trim();
        return Path.IsPathRooted(path) || (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile);
    }

    //CSJ,method=ReadProjectAssetNames,プロジェクトの素材名を読み取る
    private static bool ReadProjectAssetNames(JsonElement node, HashSet<string> names)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            //CSJ,variable=property,プロパティ
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name.Equals(YmmpxTransfer.EvidenceProperty, StringComparison.OrdinalIgnoreCase))
                    return true;
                if ((property.Name.Equals("FilePath", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("EnableLayersFilePath", StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } reference)
                {
                    if (IsTransferAssetReference(reference))
                        return true;
                    names.Add(Path.GetFileName(PackagingDetector.NormalizeFilePathKey(reference)));
                }
                else if (ReadProjectAssetNames(property.Value, names))
                    return true;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
                if (ReadProjectAssetNames(item, names))
                    return true;
        return false;
    }

    //CSJ,method=IsTransferAssetReference,受け渡し素材参照かを判定する
    private static bool IsTransferAssetReference(string reference)
    {
        var components = reference.Replace('\\', '/').Split('/');
        if (components.Length >= 2 && components[^2].Equals("resources", StringComparison.OrdinalIgnoreCase) &&
            YmmpxTransfer.IsLibraryMemberName(components[^1])) return true;
        for (var i = 0; i < components.Length - 2; i++)
        {
            if (!components[i].Equals("ymmpm-missing", StringComparison.OrdinalIgnoreCase) &&
                !(i > 0 && components[i - 1].Equals("resources", StringComparison.OrdinalIgnoreCase) &&
                    components[i].Equals("ymmpm-transfer", StringComparison.OrdinalIgnoreCase)))
                continue;
            //CSJ,variable=key,キー
            var key = components[i + 1];
            if (key.Length == 8 && key.All(Uri.IsHexDigit) && components[i + 2].Length > 0)
                return true;
        }
        return false;
    }

    //CSJ,method=ReadAsset,素材を読み取る
    private static void ReadAsset(JsonElement entry, string nameProperty, HashSet<string> names, HashSet<string> hashes)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("Sha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
            hash.GetString() is not { Length: 64 } value || !value.All(Uri.IsHexDigit))
            return;
        hashes.Add(value);
        if (entry.TryGetProperty(nameProperty, out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { } path)
            names.Add(Path.GetFileName(PackagingDetector.NormalizeFilePathKey(path)));
    }
}
