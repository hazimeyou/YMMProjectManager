using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YMMProjectManager.Domain.Packaging;
using static YMMProjectManager.Infrastructure.OwnedFilePublication;

namespace YMMProjectManager.Infrastructure.Packaging;

internal static class YmmpxTransfer
{
    internal const string ManifestName = "ymmpm-transfer.json";
    internal const string ArchiveCommentPrefix = "YMMProjectManager transfer v1:";
    internal const string MediaPrefix = "resources/ymmpm-transfer/";
    internal const string MissingPrefix = "ymmpm-missing/";
    internal const int MaxManifestBytes = 16 * 1024 * 1024;
    private const int MaxAssets = 10000 - 3;
    private const long MaxPackageBytes = 100L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    internal sealed record Manifest(int Version, string PackageName, string PackageSha256, Asset[] Assets, MissingAsset[] Missing);
    internal sealed record Asset(string ArchivePath, string OriginalFileName, long Length, string Sha256, YmmpxTransferPermission Permission);
    internal sealed record MissingAsset(string ReferencePath, string OriginalFileName, long? Length, string? Sha256,
        YmmpxTransferPermission Permission);
    private sealed record ManifestContent(string PackageName, IReadOnlyList<Asset> Assets, IReadOnlyList<MissingAsset> Missing);
    private sealed record Reference(JsonObject Owner, string Name, string SourcePath, string OriginalName, bool IsPngSequence);

    internal static async Task<YmmpxTransferPlan> InspectAsync(string projectPath, CancellationToken token,
        IReadOnlyList<string>? allowedMaterialRoots)
    {
        token.ThrowIfCancellationRequested();
        var sourcePath = AssetPool.FullOrdinaryPath(Path.GetFullPath(projectPath));
        RequireLocalPath(sourcePath);
        var parent = Path.GetDirectoryName(sourcePath)!;
        using var fence = DirectoryFence.Open(parent, enforceShare: true);
        await using var source = OpenOrdinaryRead(sourcePath);
        var (project, hash) = await ReadProjectAsync(source, sourcePath, token).ConfigureAwait(false);
        var references = ReadReferences(project, parent);
        var roots = (allowedMaterialRoots ?? []).Select(AssetPool.FullOrdinaryPath).Append(parent).Select(root =>
        {
            RequireLocalPath(root);
            return Path.TrimEndingDirectorySeparator(root);
        }).Distinct(StringComparer.Ordinal).ToArray();

        var candidates = new List<YmmpxTransferCandidate>();
        foreach (var group in references.GroupBy(reference => reference.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var reference = group.First();
            string? reason = null;
            if (group.Any(item => !string.Equals(item.SourcePath, reference.SourcePath, StringComparison.Ordinal)))
                reason = "大文字と小文字が異なる参照の同一性を確認できません。";
            else if (group.Any(item => item.IsPngSequence))
                reason = "連番 PNG はこの受け渡しで同梱できません。";
            else if (!IsPortableName(reference.OriginalName) || Path.GetExtension(reference.OriginalName).Equals(".ymmp", StringComparison.OrdinalIgnoreCase))
                reason = "受け渡しに使用できない素材名です。";
            else if (!roots.Any(root => IsWithin(reference.SourcePath, root)))
                reason = "許可したローカルフォルダー外の参照です。";
            else if (string.Equals(sourcePath, reference.SourcePath, StringComparison.OrdinalIgnoreCase))
                reason = "元のプロジェクトと同じファイルです。";

            if (reason is null)
            {
                try
                {
                    using var assetFence = DirectoryFence.Open(Path.GetDirectoryName(reference.SourcePath)!, enforceShare: true);
                    await using var asset = OpenOrdinaryRead(reference.SourcePath);
                    if (asset.Length > MaxPackageBytes) throw new IOException();
                    var assetHash = Convert.ToHexString(await SHA256.HashDataAsync(asset, token).ConfigureAwait(false));
                    candidates.Add(new(reference.SourcePath, reference.OriginalName, asset.Length, assetHash, null));
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    reason = "素材が見つからないか読み取れません。";
                }
            }
            candidates.Add(new(reference.SourcePath, reference.OriginalName, null, null, reason));
        }
        return new YmmpxTransferPlan(sourcePath, hash, candidates.AsReadOnly());
    }

    internal static async Task<YmmpxPackageResult> CreateAsync(YmmpxTransferPlan plan,
        IReadOnlyDictionary<string, YmmpxTransferPermission> approvals, string outputDirectory,
        CancellationToken token, IProgress<double>? progress, FileLogger logger)
    {
        var final = Path.TrimEndingDirectorySeparator(AssetPool.FullOrdinaryPath(Path.GetFullPath(outputDirectory)));
        RequireLocalPath(final);
        var parent = Path.GetDirectoryName(final) ?? throw new IOException("新しい出力フォルダーを指定してください。");
        using var destinationFence = DirectoryFence.Open(parent, enforceShare: true);
        if (File.Exists(final) || Directory.Exists(final))
            throw new IOException("出力先が既に存在します。新しいフォルダー名を指定してください。");

        using var sourceFence = DirectoryFence.Open(Path.GetDirectoryName(plan.ProjectPath)!, enforceShare: true);
        await using var source = OpenOrdinaryRead(plan.ProjectPath);
        var (project, hash) = await ReadProjectAsync(source, plan.ProjectPath, token).ConfigureAwait(false);
        if (hash != plan.ProjectSha256) throw new InvalidDataException("プレビュー後に元プロジェクトが変更されました。再確認してください。");
        var references = ReadReferences(project, Path.GetDirectoryName(plan.ProjectPath)!);
        var candidates = plan.Assets.ToDictionary(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase);
        var confirmations = approvals.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, permission) in confirmations)
            if (!Enum.IsDefined(permission) || !candidates.TryGetValue(path, out var candidate) ||
                (IsApproved(permission) && !candidate.CanInclude))
                throw new InvalidDataException("プレビュー済みの読み取り可能な素材だけを同梱できます。");

        if (references.Any(reference => !candidates.ContainsKey(reference.SourcePath)))
            throw new InvalidDataException("素材の参照を再確認してください。");

        var projectName = Path.GetFileName(plan.ProjectPath);
        var packageName = Path.ChangeExtension(projectName, ".ymmpx");
        if (!IsPortableName(projectName) || !IsPortableName(packageName))
            throw new InvalidDataException("受け渡しに使用できないプロジェクト名です。");
        using var stage = OwnedStagingDirectory.Reserve(parent, ".ymmpm-transfer-");
        var packagePath = Path.Combine(stage.Path, packageName);
        var manifestPath = Path.Combine(stage.Path, ManifestName);
        var owned = new Dictionary<string, (ulong, Guid)>();
        var assets = new List<Asset>();
        var missing = new List<MissingAsset>();
        var published = false;
        try
        {
            string packageHash;
            await using (var package = CreateOwnedOutputFile(packagePath, readBack: true))
            {
                owned.Add(packagePath, ProjectFileEvidence.ReadPhysicalIdentity(package));
                using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    long totalAssetBytes = 0;
                    foreach (var candidate in plan.Assets)
                    {
                        token.ThrowIfCancellationRequested();
                        var ordinal = assigned.Count.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
                        if (confirmations.TryGetValue(candidate.SourcePath, out var permission) && IsApproved(permission))
                        {
                            var entryPath = MediaPrefix + ordinal + "/" + candidate.OriginalFileName;
                            using var assetFence = DirectoryFence.Open(Path.GetDirectoryName(candidate.SourcePath)!, enforceShare: true);
                            await using var asset = OpenOrdinaryRead(candidate.SourcePath);
                            if (asset.Length != candidate.Length || asset.Length > MaxPackageBytes - totalAssetBytes)
                                throw new InvalidDataException("プレビュー後に素材のサイズが変更されたか、合計サイズが上限を超えました。");
                            totalAssetBytes += asset.Length;
                            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            await using (var data = archive.CreateEntry(entryPath, CompressionLevel.NoCompression).Open())
                            {
                                var buffer = new byte[81920];
                                int count;
                                while ((count = await asset.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                                {
                                    sha.AppendData(buffer, 0, count);
                                    await data.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                                }
                            }
                            var digest = Convert.ToHexString(sha.GetHashAndReset());
                            if (digest != candidate.Sha256)
                                throw new InvalidDataException("プレビュー後に素材の内容が変更されました。再確認してください。");
                            assigned.Add(candidate.SourcePath, entryPath);
                            assets.Add(new(entryPath, candidate.OriginalFileName, asset.Length, digest, permission));
                        }
                        else
                        {
                            var name = IsPortableName(candidate.OriginalFileName) ? candidate.OriginalFileName : "unavailable";
                            var placeholder = MissingPrefix + ordinal + "/" + name;
                            assigned.Add(candidate.SourcePath, placeholder);
                            missing.Add(new(placeholder, name, candidate.Length, candidate.Sha256, permission));
                        }
                        progress?.Report((double)assigned.Count / (plan.Assets.Count + 1) * 0.85);
                    }

                    foreach (var reference in references)
                        reference.Owner[reference.Name] = assigned[reference.SourcePath];

                    await WriteEntryAsync(archive, "_ymmpx_project_path.txt", Encoding.UTF8.GetBytes(projectName), token).ConfigureAwait(false);
                    await WriteEntryAsync(archive, projectName, JsonSerializer.SerializeToUtf8Bytes(project), token).ConfigureAwait(false);
                    await WriteEntryAsync(archive, "links.json", JsonSerializer.SerializeToUtf8Bytes(assets.ToDictionary(asset => asset.ArchivePath,
                        asset => asset.ArchivePath, StringComparer.Ordinal)), token).ConfigureAwait(false);
                    archive.Comment = MakeArchiveComment(packageName, assets, missing);
                }
                package.Flush(flushToDisk: true);
                package.Position = 0;
                packageHash = Convert.ToHexString(await SHA256.HashDataAsync(package, token).ConfigureAwait(false));
            }

            var manifest = new Manifest(1, packageName, packageHash, assets.ToArray(), missing.ToArray());
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            if (manifestBytes.Length > MaxManifestBytes) throw new InvalidDataException("受け渡し情報が大きすぎます。");
            await using (var manifestFile = CreateOwnedOutputFile(manifestPath))
            {
                owned.Add(manifestPath, ProjectFileEvidence.ReadPhysicalIdentity(manifestFile));
                await manifestFile.WriteAsync(manifestBytes, token).ConfigureAwait(false);
                manifestFile.Flush(flushToDisk: true);
            }

            (ulong Volume, Guid FileId) stagingIdentity;
            using (var stagePin = stage.PinRecoveryPath())
            await using (var archiveInput = OpenOrdinaryRead(packagePath))
            await using (var manifestInput = OpenOrdinaryRead(manifestPath))
            {
                stagingIdentity = ProjectFileEvidence.ReadPhysicalIdentity(stagePin);
                CheckTransferFiles(stage.Path, packageName);
                if (ProjectFileEvidence.ReadPhysicalIdentity(archiveInput) != owned[packagePath] ||
                    ProjectFileEvidence.ReadPhysicalIdentity(manifestInput) != owned[manifestPath])
                    throw new IOException("検証中に作業ファイルが変更されました。");
                await YmmpxPackageReader.VerifyTransferAsync(archiveInput, manifestInput, packageName, token).ConfigureAwait(false);
            }
            // NTFS refuses directory rename while a child file is held read-open, including
            // with FILE_SHARE_DELETE. Close after validation and check the exact published files.
            stage.Publish(final, token);
            published = true;
            using var publishedFence = DirectoryFence.Open(final, enforceShare: true);
            using var publishedDirectory = OpenOrdinaryPath(final, expectDirectory: true, allowRename: false);
            if (ProjectFileEvidence.ReadPhysicalIdentity(publishedDirectory) != stagingIdentity)
                throw new IOException("公開後の出力フォルダーを検証できません。出力したフォルダーを使用しないでください。");
            CheckTransferFiles(final, packageName);
            await using var publishedArchive = OpenOrdinaryRead(Path.Combine(final, packageName));
            await using var publishedManifest = OpenOrdinaryRead(Path.Combine(final, ManifestName));
            var publishedManifestHash = await SHA256.HashDataAsync(publishedManifest, CancellationToken.None).ConfigureAwait(false);
            if (ProjectFileEvidence.ReadPhysicalIdentity(publishedArchive) != owned[packagePath] ||
                ProjectFileEvidence.ReadPhysicalIdentity(publishedManifest) != owned[manifestPath] ||
                !Convert.ToHexString(await SHA256.HashDataAsync(publishedArchive, CancellationToken.None).ConfigureAwait(false)).Equals(packageHash, StringComparison.Ordinal) ||
                !SHA256.HashData(manifestBytes).AsSpan().SequenceEqual(publishedManifestHash))
                throw new IOException("公開後の受け渡しファイルを検証できません。出力したフォルダーを使用しないでください。");
        }
        finally
        {
            if (!published) CleanupStage(stage, owned, logger);
        }

        return new YmmpxPackageResult
        {
            Success = true, OutputPath = Path.Combine(final, packageName),
            DetectedMaterialCount = candidates.Count, PackagedMaterialCount = assets.Count,
            MissingMaterialCount = missing.Count,
            Links = missing.Select(asset => new YmmpxPackageLink
            {
                OriginalPath = asset.OriginalFileName, Status = "excluded",
            }).ToArray(),
        };
    }

    private static void CheckTransferFiles(string directory, string packageName)
    {
        var names = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Take(3)
            .ToHashSet(StringComparer.Ordinal);
        if (names.Count != 2 || !names.Contains(packageName) || !names.Contains(ManifestName))
            throw new IOException("受け渡し用フォルダーに所有を確認できない項目があります。");
    }

    internal static async Task<Manifest> ReadManifestAsync(Stream stream, string packageName, CancellationToken token)
    {
        if (stream.Length is <= 0 or > MaxManifestBytes) throw new InvalidDataException("受け渡し情報のサイズが不正です。");
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        RejectDuplicateProperties(json.RootElement, token);
        var manifest = json.RootElement.Deserialize<Manifest>(JsonOptions);
        if (manifest is not { Version: 1, Assets: not null, Missing: not null } ||
            !string.Equals(manifest.PackageName, packageName, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(manifest.PackageSha256) || (long)manifest.Assets.Length + manifest.Missing.Length > MaxAssets)
            throw new InvalidDataException("受け渡し情報がパッケージに対応しません。");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets)
        {
            token.ThrowIfCancellationRequested();
            if (asset is null || !IsTransferReference(asset.ArchivePath, MediaPrefix, asset.OriginalFileName) ||
                !IsApproved(asset.Permission) || asset.Length is < 0 or > MaxPackageBytes || !IsSha256(asset.Sha256) ||
                !names.Add(asset.ArchivePath))
                throw new InvalidDataException("受け渡し素材の情報が不正です。");
        }
        foreach (var missing in manifest.Missing)
        {
            token.ThrowIfCancellationRequested();
            if (missing is null || !IsTransferReference(missing.ReferencePath, MissingPrefix, missing.OriginalFileName) ||
                !Enum.IsDefined(missing.Permission) || IsApproved(missing.Permission) ||
                (missing.Length.HasValue != (missing.Sha256 is not null)) ||
                (missing.Length.HasValue && (missing.Length is < 0 or > MaxPackageBytes || !IsSha256(missing.Sha256))) ||
                !names.Add(missing.ReferencePath))
                throw new InvalidDataException("不足素材の情報が不正です。");
        }
        return manifest;
    }

    internal static string MakeArchiveComment(string packageName, IReadOnlyList<Asset> assets, IReadOnlyList<MissingAsset> missing)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new ManifestContent(packageName, assets, missing), JsonOptions);
        return ArchiveCommentPrefix + Convert.ToHexString(SHA256.HashData(content));
    }

    internal static bool IsReferenceProperty(string name) =>
        name.Equals("FilePath", StringComparison.OrdinalIgnoreCase) || name.Equals("EnableLayersFilePath", StringComparison.OrdinalIgnoreCase);

    internal static FileStream OpenOrdinaryRead(string path)
    {
        var handle = OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void RequireLocalPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(path) is not string root ||
            (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable)))
            throw new IOException("既存のローカルフォルダーを指定してください。");
    }

    private static bool IsApproved(YmmpxTransferPermission permission) =>
        permission is YmmpxTransferPermission.RedistributionAllowed or YmmpxTransferPermission.OwnWork;

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsTransferReference(string? path, string prefix, string? name)
    {
        if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal) || !IsPortableName(name)) return false;
        var parts = path[prefix.Length..].Split('/');
        return parts.Length == 2 && parts[0].Length == 8 && parts[0].All(Uri.IsHexDigit) &&
            string.Equals(parts[1], name, StringComparison.Ordinal);
    }

    private static bool IsPortableName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        AssetInventory.IsOrdinaryPathComponent(name) && !name.Any(char.IsControl);

    private static async Task<(JsonObject Project, string Sha256)> ReadProjectAsync(FileStream stream, string path, CancellationToken token)
    {
        if (!Path.GetExtension(path).Equals(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            stream.Length is <= 0 or > YmmpxPackageReader.MaxProjectBytes)
            throw new InvalidDataException("有効な .ymmp プロジェクトを指定してください。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var text = DecodeProject(bytes);
        using var document = JsonDocument.Parse(text);
        RejectDuplicateProperties(document.RootElement, token);
        return (JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("プロジェクト JSON が不正です。"),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string DecodeProject(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return new UTF32Encoding(false, false, true).GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return new UTF32Encoding(true, false, true).GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return new UnicodeEncoding(false, false, true).GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return new UnicodeEncoding(true, false, true).GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static void RejectDuplicateProperties(JsonElement node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("JSON に重複した項目名があります。");
                RejectDuplicateProperties(property.Value, token);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicateProperties(child, token);
    }

    private static IReadOnlyList<Reference> ReadReferences(JsonObject project, string directory)
    {
        var references = new List<Reference>();
        Collect(project);
        return references;

        void Collect(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                var isVideo = obj["$type"] is JsonValue type && type.TryGetValue<string>(out var typeName) &&
                    typeName.StartsWith("YukkuriMovieMaker.Project.Items.VideoItem,", StringComparison.Ordinal);
                foreach (var (name, child) in obj.ToArray())
                {
                    if (IsReferenceProperty(name) && child is JsonValue value && value.TryGetValue<string>(out var path) &&
                        !string.IsNullOrWhiteSpace(path))
                    {
                        if (ReferenceEquals(obj, project) && name.Equals("FilePath", StringComparison.OrdinalIgnoreCase))
                        {
                            obj[name] = "";
                            continue;
                        }
                        var resolved = PackagingDetector.ResolveMaterialPath(path, directory);
                        var basename = resolved is not null ? Path.GetFileName(resolved) : "unavailable";
                        var isSequence = isVideo && name.Equals("FilePath", StringComparison.OrdinalIgnoreCase) &&
                            Path.GetExtension(basename).Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                            Path.GetFileNameWithoutExtension(basename).LastOrDefault() is var last && char.IsDigit(last);
                        references.Add(new(obj, name, resolved ?? path, basename, isSequence));
                        if (references.Count > MaxAssets) throw new InvalidDataException("素材参照が上限を超えています。");
                    }
                    else if (child is not null) Collect(child);
                }
            }
            else if (node is JsonArray array)
                foreach (var child in array) if (child is not null) Collect(child);
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] data, CancellationToken token)
    {
        await using var output = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await output.WriteAsync(data, token).ConfigureAwait(false);
    }

    private static void CleanupStage(OwnedStagingDirectory stage, Dictionary<string, (ulong, Guid)> owned, FileLogger logger)
    {
        try
        {
            using (stage.PinRecoveryPath())
                foreach (var (path, identity) in owned)
                {
                    using var handle = OpenOrdinaryPath(path, expectDirectory: false, allowRename: true);
                    if (ProjectFileEvidence.ReadPhysicalIdentity(handle) == identity) TryDeleteOpenedPath(handle);
                }
            if (!stage.DeleteIfEmpty()) logger.Info("YMMPX 受け渡しの作業フォルダーを保持しました。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Info($"YMMPX 受け渡しの作業フォルダーを保全しました。error={ex.GetType().Name}");
        }
    }
}
