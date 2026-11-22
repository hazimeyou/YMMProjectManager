using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YMMProjectManager.Domain.Packaging;
using static YMMProjectManager.Infrastructure.OwnedFilePublication;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxTransfer,YMMPXパッケージの受け渡し
internal static class YmmpxTransfer
{
    //CSJ,field=ManifestName,目録名前
    internal const string ManifestName = "ymmpm-transfer.json";
    internal const string EvidenceProperty = "YMMProjectManagerTransferV2";
    internal const string LibraryMediaPrefix = "resources/ymmpm-asset-";
    internal const string ArchiveCommentPrefix = "YMMProjectManager transfer v1:";
    //CSJ,field=MediaPrefix,メディア接頭辞
    internal const string MediaPrefix = "resources/ymmpm-transfer/";
    //CSJ,field=MissingPrefix,欠落接頭辞
    internal const string MissingPrefix = "ymmpm-missing/";
    //CSJ,field=MaxManifestBytes,最大目録バイト列
    internal const int MaxManifestBytes = 16 * 1024 * 1024;
    //CSJ,field=MaxAssets,最大素材一覧
    private const int MaxAssets = 10000 - 3;
    //CSJ,field=MaxPackageBytes,最大パッケージバイト列
    private const long MaxPackageBytes = 100L * 1024 * 1024 * 1024;

    //CSJ,field=JsonOptions,JSONオプション
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    //CSJ,record=Manifest,目録
    internal sealed record Manifest(int Version, string PackageName, string PackageSha256, Asset[] Assets, MissingAsset[] Missing);
    //CSJ,record=Asset,素材
    internal sealed record Asset(string ArchivePath, string OriginalFileName, long Length, string Sha256, YmmpxTransferPermission Permission);
    //CSJ,record=MissingAsset,欠落素材
    internal sealed record MissingAsset(string ReferencePath, string OriginalFileName, long? Length, string? Sha256,
        YmmpxTransferPermission Permission);
    //CSJ,record=ManifestContent,目録内容
    private sealed record ManifestContent(string PackageName, IReadOnlyList<Asset> Assets, IReadOnlyList<MissingAsset> Missing);
    //CSJ,record=Reference,参照
    private sealed record Reference(JsonObject Owner, string Name, string SourcePath, string OriginalName, bool IsPngSequence);

    //CSJ,method=InspectAsync,点検する（非同期）
    internal static async Task<YmmpxTransferPlan> InspectAsync(string projectPath, CancellationToken token,
        IReadOnlyList<string>? allowedMaterialRoots)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=sourcePath,元のパス
        var sourcePath = AssetPool.FullOrdinaryPath(Path.GetFullPath(projectPath));
        RequireLocalPath(sourcePath);
        var parent = Path.GetDirectoryName(sourcePath)!;
        //CSJ,variable=fence,整合性境界
        using var fence = DirectoryFence.Open(parent, enforceShare: true);
        await using var source = OpenOrdinaryRead(sourcePath);
        var (project, hash) = await ReadProjectAsync(source, sourcePath, token).ConfigureAwait(false);
        //CSJ,variable=references,参照一覧
        var references = ReadReferences(project, parent);
        var roots = (allowedMaterialRoots ?? []).Select(AssetPool.FullOrdinaryPath).Append(parent).Select(root =>
        {
            RequireLocalPath(root);
            return Path.TrimEndingDirectorySeparator(root);
        }).Distinct(StringComparer.Ordinal).ToArray();

        //CSJ,variable=candidates,候補一覧
        var candidates = new List<YmmpxTransferCandidate>();
        //CSJ,variable=group,グループ
        foreach (var group in references.GroupBy(reference => reference.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=reference,参照
            var reference = group.First();
            //CSJ,variable=reason,理由
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
                    //CSJ,variable=assetFence,素材整合性境界
                    using var assetFence = DirectoryFence.Open(Path.GetDirectoryName(reference.SourcePath)!, enforceShare: true);
                    //CSJ,variable=asset,素材
                    await using var asset = OpenOrdinaryRead(reference.SourcePath);
                    if (asset.Length > MaxPackageBytes) throw new IOException();
                    //CSJ,variable=assetHash,素材ハッシュ
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

    //CSJ,method=CreateAsync,作成する（非同期）
    internal static async Task<YmmpxPackageResult> CreateAsync(YmmpxTransferPlan plan,
        IReadOnlyDictionary<string, YmmpxTransferPermission> approvals, string outputDirectory,
        CancellationToken token, IProgress<double>? progress, FileLogger logger, YmmpxLibBridge bridge)
    {
        //CSJ,variable=final,最終
        var final = Path.TrimEndingDirectorySeparator(AssetPool.FullOrdinaryPath(Path.GetFullPath(outputDirectory)));
        RequireLocalPath(final);
        var parent = Path.GetDirectoryName(final) ?? throw new IOException("新しい出力フォルダーを指定してください。");
        //CSJ,variable=destinationFence,格納先整合性境界
        using var destinationFence = DirectoryFence.Open(parent, enforceShare: true);
        if (File.Exists(final) || Directory.Exists(final))
            throw new IOException("出力先が既に存在します。新しいフォルダー名を指定してください。");

        //CSJ,variable=sourceFence,元整合性境界
        using var sourceFence = DirectoryFence.Open(Path.GetDirectoryName(plan.ProjectPath)!, enforceShare: true);
        await using var source = OpenOrdinaryRead(plan.ProjectPath);
        var (project, hash) = await ReadProjectAsync(source, plan.ProjectPath, token).ConfigureAwait(false);
        if (hash != plan.ProjectSha256) throw new InvalidDataException("プレビュー後に元プロジェクトが変更されました。再確認してください。");
        //CSJ,variable=references,参照一覧
        var references = ReadReferences(project, Path.GetDirectoryName(plan.ProjectPath)!);
        //CSJ,variable=candidates,候補一覧
        var candidates = plan.Assets.ToDictionary(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase);
        var confirmations = approvals.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, permission) in confirmations)
            if (!Enum.IsDefined(permission) || !candidates.TryGetValue(path, out var candidate) ||
                (IsApproved(permission) && !candidate.CanInclude))
                throw new InvalidDataException("プレビュー済みの読み取り可能な素材だけを同梱できます。");

        if (references.Any(reference => !candidates.ContainsKey(reference.SourcePath)))
            throw new InvalidDataException("素材の参照を再確認してください。");

        //CSJ,variable=projectName,プロジェクト名
        var projectName = Path.GetFileName(plan.ProjectPath);
        //CSJ,variable=packageName,パッケージ名前
        var packageName = Path.ChangeExtension(projectName, ".ymmpx");
        if (!IsPortableName(projectName) || !IsPortableName(packageName))
            throw new InvalidDataException("受け渡しに使用できないプロジェクト名です。");
        //CSJ,variable=stage,作業段階
        using var stage = OwnedStagingDirectory.Reserve(parent, ".ymmpm-transfer-");
        //CSJ,variable=packagePath,パッケージのパス
        var packagePath = Path.Combine(stage.Path, packageName);
        //CSJ,variable=manifestPath,目録パス
        var manifestPath = Path.Combine(stage.Path, ManifestName);
        //CSJ,variable=owned,管理対象の
        var owned = new Dictionary<string, (ulong, Guid)>();
        //CSJ,variable=assets,素材一覧
        var assets = new List<Asset>();
        //CSJ,variable=missing,欠落
        var missing = new List<MissingAsset>();
        //CSJ,variable=published,確定済み
        var published = false;
        try
        {
            if (!bridge.TryEnsureAvailable(out _))
                return new YmmpxPackageResult { ErrorMessage = bridge.MissingLibraryMessage };
            if (project.Any(field => field.Key.Equals(EvidenceProperty, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("プロジェクトに受け渡し用の予約項目があります。元ファイルを保持しました。");

            var packed = await PackApprovedInputsAsync(parent, plan, confirmations, project, references, packagePath,
                assets, missing, token, progress, bridge, logger).ConfigureAwait(false);
            if (!packed.Success) return packed;
            owned.Add(packagePath, packed.PublishedIdentity ?? throw new IOException("YMMPX 出力を識別できません。"));
            string packageHash;
            await using (var package = OpenOrdinaryRead(packagePath))
            {
                if (ProjectFileEvidence.ReadPhysicalIdentity(package) != owned[packagePath])
                    throw new IOException("YMMPX 出力が置換されました。");
                packageHash = Convert.ToHexString(await SHA256.HashDataAsync(package, token).ConfigureAwait(false));
            }

            //CSJ,variable=manifest,目録
            var manifest = new Manifest(2, packageName, packageHash, assets.ToArray(), missing.ToArray());
            //CSJ,variable=manifestBytes,目録バイト列
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            if (manifestBytes.Length > MaxManifestBytes) throw new InvalidDataException("受け渡し情報が大きすぎます。");
            //CSJ,variable=manifestFile,目録ファイル
            await using (var manifestFile = CreateOwnedOutputFile(manifestPath))
            {
                owned.Add(manifestPath, ProjectFileEvidence.ReadPhysicalIdentity(manifestFile));
                await manifestFile.WriteAsync(manifestBytes, token).ConfigureAwait(false);
                manifestFile.Flush(flushToDisk: true);
            }

            //CSJ,variable=stagingIdentity,準備領域識別情報
            (ulong Volume, Guid FileId) stagingIdentity;
            //CSJ,variable=stagePin,作業段階固定参照
            using (var stagePin = stage.PinRecoveryPath())
            //CSJ,variable=archiveInput,アーカイブ入力
            await using (var archiveInput = OpenOrdinaryRead(packagePath))
            //CSJ,variable=manifestInput,目録入力
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
            //CSJ,variable=publishedFence,確定済み整合性境界
            using var publishedFence = DirectoryFence.Open(final, enforceShare: true);
            //CSJ,variable=publishedDirectory,確定済みのディレクトリ
            using var publishedDirectory = OpenOrdinaryPath(final, expectDirectory: true, allowRename: false);
            if (ProjectFileEvidence.ReadPhysicalIdentity(publishedDirectory) != stagingIdentity)
                throw new IOException("公開後の出力フォルダーを検証できません。出力したフォルダーを使用しないでください。");
            CheckTransferFiles(final, packageName);
            //CSJ,variable=publishedArchive,確定済みアーカイブ
            await using var publishedArchive = OpenOrdinaryRead(Path.Combine(final, packageName));
            //CSJ,variable=publishedManifest,確定済み目録
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

    private static async Task<YmmpxPackageResult> PackApprovedInputsAsync(string parent, YmmpxTransferPlan plan,
        IReadOnlyDictionary<string, YmmpxTransferPermission> confirmations, JsonObject project, IReadOnlyList<Reference> references,
        string packagePath, List<Asset> assets, List<MissingAsset> missing, CancellationToken token,
        IProgress<double>? progress, YmmpxLibBridge bridge, FileLogger logger)
    {
        using var inputs = OwnedStagingDirectory.Reserve(parent, ".ymmpm-transfer-");
        var inputIds = new Dictionary<string, (ulong, Guid)>();
        try
        {
            var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var libraryFiles = new JsonArray();
            long totalBytes = 0;
            foreach (var candidate in plan.Assets)
            {
                token.ThrowIfCancellationRequested();
                var ordinal = assigned.Count.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
                confirmations.TryGetValue(candidate.SourcePath, out var permission);
                if (IsApproved(permission))
                {
                    // End the stem with a letter so a single VideoItem cannot discover a PNG sequence.
                    var name = "ymmpm-asset-" + ordinal + "-file" + Path.GetExtension(candidate.OriginalFileName);
                    var member = "resources/" + name;
                    using var fence = DirectoryFence.Open(Path.GetDirectoryName(candidate.SourcePath)!, enforceShare: true);
                    await using var asset = OpenOrdinaryRead(candidate.SourcePath);
                    if (asset.Length != candidate.Length || asset.Length > MaxPackageBytes - totalBytes)
                        throw new InvalidDataException("素材のサイズまたは合計サイズを再確認してください。");
                    totalBytes += asset.Length;
                    var copyPath = Path.Combine(inputs.Path, name);
                    await using var copy = CreateOwnedOutputFile(copyPath, readBack: true);
                    inputIds.Add(copyPath, ProjectFileEvidence.ReadPhysicalIdentity(copy));
                    await asset.CopyToAsync(copy, token).ConfigureAwait(false);
                    copy.Position = 0;
                    var digest = Convert.ToHexString(await SHA256.HashDataAsync(copy, token).ConfigureAwait(false));
                    if (digest != candidate.Sha256)
                        throw new InvalidDataException("プレビュー後に素材の内容が変更されました。");
                    assigned.Add(candidate.SourcePath, member);
                    assets.Add(new(member, candidate.OriginalFileName, asset.Length, digest, permission));
                    // YmmpxLib collects FilePath. Also expose materials referenced only by EnableLayersFilePath.
                    libraryFiles.Add(new JsonObject { ["FilePath"] = name });
                }
                else
                {
                    var name = IsPortableName(candidate.OriginalFileName) ? candidate.OriginalFileName : "unavailable";
                    var placeholder = MissingPrefix + ordinal + "/" + name;
                    assigned.Add(candidate.SourcePath, placeholder);
                    excluded.Add(Path.GetFullPath(placeholder, inputs.Path));
                    missing.Add(new(placeholder, name, candidate.Length, candidate.Sha256, permission));
                }
                progress?.Report((double)assigned.Count / (plan.Assets.Count + 1) * 0.4);
            }

            SetPackageReferences(references, assigned);
            project[EvidenceProperty] = new JsonObject
            {
                ["ManifestDigest"] = ManifestDigest(Path.GetFileName(packagePath), assets, missing),
                ["Materials"] = libraryFiles,
            };
            var inputProject = Path.Combine(inputs.Path, Path.GetFileName(plan.ProjectPath));
            var projectBytes = JsonSerializer.SerializeToUtf8Bytes(project);
            await using (var snapshot = CreateOwnedOutputFile(inputProject))
            {
                inputIds.Add(inputProject, ProjectFileEvidence.ReadPhysicalIdentity(snapshot));
                await snapshot.WriteAsync(projectBytes, token).ConfigureAwait(false);
            }
            // Pin the exact approved inputs while the path-based library is reading them.
            var pins = new List<FileStream>();
            try
            {
                foreach (var (path, id) in inputIds)
                {
                    var pin = OpenOrdinaryRead(path);
                    pins.Add(pin);
                    if (ProjectFileEvidence.ReadPhysicalIdentity(pin) != id)
                        throw new IOException("同梱準備中のファイルが変更されました。");
                    if (path == inputProject)
                    {
                        if (pin.Length != projectBytes.Length)
                            throw new IOException("同梱前に受け渡し用プロジェクトのサイズが変更されました。");
                        var actualHash = await SHA256.HashDataAsync(pin, token).ConfigureAwait(false);
                        if (!SHA256.HashData(projectBytes).AsSpan().SequenceEqual(actualHash))
                            throw new IOException("同梱前に受け渡し用プロジェクトが変更されました。");
                    }
                }

                return await bridge.CreatePackageAsync(inputProject, packagePath, excluded, token,
                    progress is null ? null : new PackagingProgress(progress)).ConfigureAwait(false);
            }
            finally { foreach (var pin in pins) pin.Dispose(); }
        }
        finally { CleanupStage(inputs, inputIds, logger); }
    }

    private static void SetPackageReferences(IReadOnlyList<Reference> references, IReadOnlyDictionary<string, string> assigned)
    {
        foreach (var reference in references)
        {
            var member = assigned[reference.SourcePath];
            if (reference.Name.Equals("FilePath", StringComparison.OrdinalIgnoreCase) &&
                member.StartsWith(LibraryMediaPrefix, StringComparison.Ordinal) &&
                Path.GetExtension(member).Equals(".psd", StringComparison.OrdinalIgnoreCase) &&
                !reference.Owner.Any(p => p.Key.Equals("EnableLayersFilePath", StringComparison.OrdinalIgnoreCase)) &&
                reference.Owner.Any(p => p.Key.Equals("EnableLayers", StringComparison.OrdinalIgnoreCase) ||
                    p.Key.Equals("EnableLayerPaths", StringComparison.OrdinalIgnoreCase)))
                reference.Owner["EnableLayersFilePath"] = member;
            reference.Owner[reference.Name] = reference.Name.Equals("FilePath", StringComparison.OrdinalIgnoreCase)
                && member.StartsWith(LibraryMediaPrefix, StringComparison.Ordinal) ? Path.GetFileName(member) : member;
        }
    }

    //CSJ,method=CheckTransferFiles,受け渡しファイル一覧を確認する
    private static void CheckTransferFiles(string directory, string packageName)
    {
        //CSJ,variable=names,名前一覧
        var names = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Take(3)
            .ToHashSet(StringComparer.Ordinal);
        if (names.Count != 2 || !names.Contains(packageName) || !names.Contains(ManifestName))
            throw new IOException("受け渡し用フォルダーに所有を確認できない項目があります。");
    }

    //CSJ,method=ReadManifestAsync,目録を読み取る（非同期）
    internal static async Task<Manifest> ReadManifestAsync(Stream stream, string packageName, CancellationToken token)
    {
        if (stream.Length is <= 0 or > MaxManifestBytes) throw new InvalidDataException("受け渡し情報のサイズが不正です。");
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        RejectDuplicateProperties(json.RootElement, token);
        //CSJ,variable=manifest,目録
        var manifest = json.RootElement.Deserialize<Manifest>(JsonOptions);
        if (manifest is not { Version: 1 or 2, Assets: not null, Missing: not null } ||
            !string.Equals(manifest.PackageName, packageName, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(manifest.PackageSha256) || (long)manifest.Assets.Length + manifest.Missing.Length > MaxAssets)
            throw new InvalidDataException("受け渡し情報がパッケージに対応しません。");

        //CSJ,variable=names,名前一覧
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=asset,素材
        foreach (var asset in manifest.Assets)
        {
            token.ThrowIfCancellationRequested();
            if (asset is null || !(manifest.Version == 1
                ? IsTransferReference(asset.ArchivePath, MediaPrefix, asset.OriginalFileName)
                : IsLibraryMember(asset.ArchivePath, asset.OriginalFileName)) ||
                !IsApproved(asset.Permission) || asset.Length is < 0 or > MaxPackageBytes || !IsSha256(asset.Sha256) ||
                !names.Add(asset.ArchivePath))
                throw new InvalidDataException("受け渡し素材の情報が不正です。");
        }
        //CSJ,variable=missing,欠落
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
        => ArchiveCommentPrefix + ManifestDigest(packageName, assets, missing);

    internal static string ManifestDigest(string packageName, IReadOnlyList<Asset> assets, IReadOnlyList<MissingAsset> missing)
    {
        //CSJ,variable=content,内容
        var content = JsonSerializer.SerializeToUtf8Bytes(new ManifestContent(packageName, assets, missing), JsonOptions);
        return Convert.ToHexString(SHA256.HashData(content));
    }

    //CSJ,method=IsReferenceProperty,参照プロパティかを判定する
    internal static bool IsReferenceProperty(string name) =>
        name.Equals("FilePath", StringComparison.OrdinalIgnoreCase) || name.Equals("EnableLayersFilePath", StringComparison.OrdinalIgnoreCase);

    //CSJ,method=OpenOrdinaryRead,通常読み取りを開く
    internal static FileStream OpenOrdinaryRead(string path)
    {
        var handle = OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    //CSJ,method=RequireLocalPath,要求ローカルパス
    private static void RequireLocalPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(path) is not string root ||
            (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable)))
            throw new IOException("既存のローカルフォルダーを指定してください。");
    }

    //CSJ,method=IsApproved,承認済みかを判定する
    private static bool IsApproved(YmmpxTransferPermission permission) =>
        permission is YmmpxTransferPermission.RedistributionAllowed or YmmpxTransferPermission.OwnWork;

    //CSJ,method=IsSha256,SHA-256かを判定する
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    //CSJ,method=IsTransferReference,受け渡し参照かを判定する
    private static bool IsTransferReference(string? path, string prefix, string? name)
    {
        if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal) || !IsPortableName(name)) return false;
        //CSJ,variable=parts,構成要素
        var parts = path[prefix.Length..].Split('/');
        return parts.Length == 2 && parts[0].Length == 8 && parts[0].All(Uri.IsHexDigit) &&
            string.Equals(parts[1], name, StringComparison.Ordinal);
    }

    private static bool IsPortableName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        AssetInventory.IsOrdinaryPathComponent(name) && !name.Any(char.IsControl);

    //CSJ,method=ReadProjectAsync,プロジェクトを読み取る（非同期）
    private static async Task<(JsonObject Project, string Sha256)> ReadProjectAsync(FileStream stream, string path, CancellationToken token)
    {
        if (!Path.GetExtension(path).Equals(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            stream.Length is <= 0 or > YmmpxPackageReader.MaxProjectBytes)
            throw new InvalidDataException("有効な .ymmp プロジェクトを指定してください。");
        //CSJ,variable=bytes,バイト列
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        //CSJ,variable=text,テキスト
        var text = DecodeProject(bytes);
        //CSJ,variable=document,文書
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

    //CSJ,method=RejectDuplicateProperties,重複プロパティ一覧を拒否する
    private static void RejectDuplicateProperties(JsonElement node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.ValueKind == JsonValueKind.Object)
        {
            //CSJ,variable=names,名前一覧
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            //CSJ,variable=property,プロパティ
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("JSON に重複した項目名があります。");
                RejectDuplicateProperties(property.Value, token);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicateProperties(child, token);
    }

    //CSJ,method=ReadReferences,参照一覧を読み取る
    private static IReadOnlyList<Reference> ReadReferences(JsonObject project, string directory)
    {
        //CSJ,variable=references,参照一覧
        var references = new List<Reference>();
        Collect(project);
        return references;

        //CSJ,method=Collect,収集する
        void Collect(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                //CSJ,variable=isVideo,動画かどうか
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
                        //CSJ,variable=resolved,解決済み
                        var resolved = PackagingDetector.ResolveMaterialPath(path, directory);
                        var basename = resolved is not null ? Path.GetFileName(resolved) : "unavailable";
                        //CSJ,variable=isSequence,順序かどうか
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

    private sealed class PackagingProgress(IProgress<double> target) : IProgress<double>
    {
        public void Report(double value) => target.Report(0.4 + 0.55 * Math.Clamp(value, 0, 1));
    }

    internal static bool IsLibraryMemberName(string name)
    {
        const string prefix = "ymmpm-asset-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !IsPortableName(name)) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length == prefix.Length + 13 && stem.EndsWith("-file", StringComparison.OrdinalIgnoreCase) &&
            stem.Substring(prefix.Length, 8).All(Uri.IsHexDigit);
    }

    private static bool IsLibraryMember(string? member, string? originalName)
    {
        if (!IsPortableName(originalName) || member is null || !member.StartsWith(LibraryMediaPrefix, StringComparison.Ordinal))
            return false;
        var tail = member[LibraryMediaPrefix.Length..];
        return tail.Length >= 13 && tail[..8].All(Uri.IsHexDigit) &&
            tail[8..] == "-file" + Path.GetExtension(originalName);
    }

    //CSJ,method=CleanupStage,後片付け作業段階
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
