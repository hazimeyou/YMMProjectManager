using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using YMMProjectManager.Domain;
using YMMProjectManager.Domain.Packaging;

using static YMMProjectManager.Infrastructure.OwnedFilePublication;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxPackageReader,YMMPXパッケージ読み取り元
internal static class YmmpxPackageReader
{
    //CSJ,field=MaxMetadataBytes,最大メタデータバイト列
    private const int MaxMetadataBytes = 16 * 1024 * 1024;
    //CSJ,field=MaxProjectBytes,最大プロジェクトバイト列
    internal const int MaxProjectBytes = 64 * 1024 * 1024;
    internal const string RetainedStagingPath = "YmmpxRetainedStagingPath";
    private static readonly uint[] CrcTable = MakeCrcTable();

    //CSJ,record=Package,パッケージ
    private sealed record Package(Dictionary<string, ZipArchiveEntry> Files, string ProjectName, JsonObject Project,
        HashSet<string> Resources, bool HasLegacyAliases);
    //CSJ,record=OwnedPath,管理対象のパス
    private sealed record OwnedPath(string Path, FileIdentity? Identity);
    //CSJ,record=ProjectSource,プロジェクト元
    private sealed record ProjectSource(string Directory, JsonElement Document)
    {
        //CSJ,property=MaterialPaths,素材パス一覧
        public Dictionary<string, string> MaterialPaths { get; } = new(StringComparer.Ordinal);
    }

    //CSJ,method=ValidateAsync,検証する（非同期）
    internal static async Task ValidateAsync(Stream source, string projectPath, JsonElement expectedProject, CancellationToken token)
    {
        //CSJ,variable=archive,アーカイブ
        using var archive = OpenValidated(source, token);
        //CSJ,variable=projectSource,プロジェクト元
        var projectSource = new ProjectSource(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, expectedProject);
        //CSJ,variable=package,パッケージ
        var package = await ReadPackageAsync(archive, token, projectSource).ConfigureAwait(false);
        if (!string.Equals(package.ProjectName, Path.GetFileName(projectPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("同梱したプロジェクト名が選択したプロジェクトと一致しません。");
        //CSJ,variable=resource,リソース
        foreach (var resource in package.Resources)
        {
            var entry = package.Files[resource];
            await using var input = entry.Open();
            if (!projectSource.MaterialPaths.TryGetValue(resource, out var materialPath))
            {
                await CopyEntryAsync(input, Stream.Null, entry.Length, entry.Crc32, token).ConfigureAwait(false);
                continue;
            }

            //CSJ,variable=material,素材
            await using var material = new FileStream(materialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            //CSJ,variable=mismatch,不一致
            const string mismatch = "同梱した素材が元のファイルと一致しません。";
            if (material.Length != entry.Length) throw new InvalidDataException(mismatch);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await CopyEntryAsync(input, Stream.Null, entry.Length, entry.Crc32, token, digest).ConfigureAwait(false);
            //CSJ,variable=materialHash,素材ハッシュ
            var materialHash = await SHA256.HashDataAsync(material, token).ConfigureAwait(false);
            if (!materialHash.AsSpan().SequenceEqual(digest.GetHashAndReset())) throw new InvalidDataException(mismatch);
        }
    }

    //CSJ,method=VerifyTransferAsync,受け渡しを検証する（非同期）
    internal static async Task VerifyTransferAsync(Stream source, Stream manifest, string packageName, CancellationToken token)
    {
        //CSJ,variable=archive,アーカイブ
        using var archive = OpenValidated(source, token);
        //CSJ,variable=package,パッケージ
        var package = await ReadPackageAsync(archive, token).ConfigureAwait(false);
        await VerifyTransferAsync(source, manifest, archive.Comment, packageName, package, token).ConfigureAwait(false);
    }

    //CSJ,method=ExtractAsync,抽出する（非同期）
    internal static async Task<YmmpxExtractResult> ExtractAsync(string packagePath, string destination, CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<double>? progress, bool requireTransfer = false)
    {
        //CSJ,variable=final,最終
        var final = Path.TrimEndingDirectorySeparator(AssetPool.FullOrdinaryPath(Path.GetFullPath(destination)));
        var parent = Path.GetDirectoryName(final);
        if (parent is null || final.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(parent) ||
            new DriveInfo(Path.GetPathRoot(final)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("展開先には、既存のローカル親フォルダーの直下に新しいフォルダーを指定してください。");
        //CSJ,variable=parentFence,親整合性境界
        using var parentFence = DirectoryFence.Open(parent);
        if (File.Exists(final) || Directory.Exists(final))
            throw new IOException("展開先が既に存在します。新しいフォルダー名を指定してください。");

        packagePath = Path.GetFullPath(packagePath);
        //CSJ,variable=packageDirectory,パッケージディレクトリ
        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        //CSJ,variable=packageFence,パッケージ整合性境界
        using var packageFence = DirectoryFence.Open(packageDirectory, enforceShare: true);
        await using var source = YmmpxTransfer.OpenOrdinaryRead(packagePath);
        //CSJ,variable=archive,アーカイブ
        using var archive = OpenValidated(source, token);
        //CSJ,variable=package,パッケージ
        var package = await ReadPackageAsync(archive, token).ConfigureAwait(false);
        //CSJ,variable=manifestPath,目録パス
        var manifestPath = Path.Combine(packageDirectory, YmmpxTransfer.ManifestName);
        //CSJ,variable=isTransfer,受け渡しかどうか
        var isTransfer = requireTransfer || archive.Comment.StartsWith(YmmpxTransfer.ArchiveCommentPrefix, StringComparison.Ordinal) ||
            package.Project.ContainsKey(YmmpxTransfer.EvidenceProperty) ||
            package.Resources.Any(path => path.StartsWith(YmmpxTransfer.MediaPrefix, StringComparison.OrdinalIgnoreCase) ||
                (path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) &&
                 YmmpxTransfer.IsLibraryMemberName(path["resources/".Length..]))) ||
            ContainsTransferPlaceholder(package.Project, token);
        //CSJ,variable=manifestInput,目録入力
        await using var manifestInput = isTransfer ? YmmpxTransfer.OpenOrdinaryRead(manifestPath) : null;
        //CSJ,variable=transfer,受け渡し
        var transfer = manifestInput is not null
            ? await VerifyTransferAsync(source, manifestInput, archive.Comment, Path.GetFileName(packagePath), package, token).ConfigureAwait(false)
            : null;
        // Restore the original basename for strict relink, including distinct generations of a same-named asset.
        var restoredNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (transfer is { Version: 2 })
            foreach (var asset in transfer.Assets)
            {
                var ordinal = asset.ArchivePath.Substring(YmmpxTransfer.LibraryMediaPrefix.Length, 8);
                restoredNames.Add(asset.ArchivePath, YmmpxTransfer.MediaPrefix + ordinal + "/" + asset.OriginalFileName);
            }
        var replacements = RewriteReferences(package.Project, package.Resources, final, token, transfer, package.HasLegacyAliases, restoredNames);

        token.ThrowIfCancellationRequested();
        //CSJ,variable=stage,作業段階
        using var stage = OwnedStagingDirectory.Reserve(parent, ".ymmpx-");
        //CSJ,variable=staging,準備領域
        var staging = stage.Path;
        //CSJ,variable=files,ファイル一覧
        var files = new List<OwnedPath>();
        //CSJ,variable=verifiedStagedFiles,検証済み準備済みファイル一覧
        var verifiedStagedFiles = new Dictionary<string, ProjectFileIdentity>(StringComparer.Ordinal);
        //CSJ,variable=directories,ディレクトリ一覧
        var directories = new List<OwnedPath>();
        //CSJ,variable=createdDirectories,作成済みディレクトリ一覧
        var createdDirectories = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
        using var subdirectoryFence = new DirectoryFence(enforceShare: true);
        //CSJ,variable=stagingHandle,準備領域ハンドル
        SafeFileHandle? stagingHandle = null;
        //CSJ,variable=published,確定済み
        var published = false;
        //CSJ,variable=restoredIdentity,復元済み識別情報
        ProjectFileIdentity? restoredIdentity = null;
        try
        {
            stagingHandle = stage.PinRecoveryPath();
            foreach (var (name, entry) in package.Files)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, restoredNames.GetValueOrDefault(name, name).Replace('/', Path.DirectorySeparatorChar));
                EnsureParents(Path.GetDirectoryName(target)!);
                RequireOrdinaryDirectory(Path.GetDirectoryName(target)!);
                await using var output = OwnedFilePublication.CreateOwnedOutputFile(target,
                    readBack: name == package.ProjectName || transfer is not null);
                files.Add(new OwnedPath(target, ReadIdentity(output.SafeFileHandle)));
                if (name == package.ProjectName)
                {
                    await JsonSerializer.SerializeAsync(output, package.Project, cancellationToken: token).ConfigureAwait(false);
                }
                else
                {
                    await using var input = entry.Open();
                    await CopyEntryAsync(input, output, entry.Length, entry.Crc32, token).ConfigureAwait(false);
                }
                if (name == package.ProjectName || transfer is not null)
                {
                    //CSJ,variable=evidence,証跡
                    var evidence = await ReadOwnedOutputEvidenceAsync(output, token).ConfigureAwait(false);
                    verifiedStagedFiles.Add(target, evidence);
                    if (name == package.ProjectName) restoredIdentity = evidence;
                }
                progress?.Report((double)files.Count / package.Files.Count * 0.99);
            }
            if (manifestInput is not null)
            {
                var target = Path.Combine(staging, YmmpxTransfer.ManifestName);
                //CSJ,variable=manifestOutput,目録出力
                await using var manifestOutput = OwnedFilePublication.CreateOwnedOutputFile(target, readBack: true);
                files.Add(new OwnedPath(target, ReadIdentity(manifestOutput.SafeFileHandle)));
                manifestInput.Position = 0;
                await manifestInput.CopyToAsync(manifestOutput, token).ConfigureAwait(false);
                verifiedStagedFiles.Add(target, await ReadOwnedOutputEvidenceAsync(manifestOutput, token).ConfigureAwait(false));
            }
            token.ThrowIfCancellationRequested();
            VerifyStagedTree(staging, token);
            await VerifyStagedFilesAsync(staging, token).ConfigureAwait(false);
            // Windows may require child directory handles to be closed before renaming.
            // Keep the staging root open and publish that exact object without replacement.
            subdirectoryFence.Dispose();
            token.ThrowIfCancellationRequested();
            //CSJ,variable=stagingIdentity,準備領域識別情報
            var stagingIdentity = ProjectFileEvidence.ReadPhysicalIdentity(stagingHandle);
            stagingHandle.Dispose();
            stagingHandle = null;
            stage.Publish(final, token);
            published = true;
            try
            {
                //CSJ,variable=publishedFence,確定済み整合性境界
                using var publishedFence = DirectoryFence.Open(final, enforceShare: true);
                //CSJ,variable=openedFinal,表示済み最終
                using var openedFinal = OpenOrdinaryDirectory(final);
                if (ProjectFileEvidence.ReadPhysicalIdentity(openedFinal) != stagingIdentity)
                    throw new IOException("展開したフォルダーが変更されました。");
                await VerifyPublishedSetAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("公開後の展開ファイルが変更されたか再検証できません。出力したフォルダーを使用しないでください。", ex);
            }
        }
        catch (Exception ex)
        {
            if (published) throw;
            var removed = false;
            try
            {
                using (stagingHandle ??= stage.PinRecoveryPath())
                {
                    //CSJ,variable=path,パス
                    foreach (var path in files) TryCleanup(path, isDirectory: false);
                    subdirectoryFence.Dispose();
                    //CSJ,variable=path,パス
                    foreach (var path in directories.AsEnumerable().Reverse()) TryCleanup(path, isDirectory: true);
                }
                stagingHandle = null;
                removed = stage.DeleteIfEmpty();
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                subdirectoryFence.Dispose();
            }
            if (!removed) ex.Data[RetainedStagingPath] = staging;
            throw;
        }
        finally
        {
            stagingHandle?.Dispose();
        }

        return new YmmpxExtractResult
        {
            Success = true,
            OutputDirectory = final,
            RestoredProjectPath = Path.Combine(final, package.ProjectName),
            RestoredProjectIdentity = restoredIdentity,
            ExtractedResourceCount = package.Resources.Count,
            ReplacedPathCount = replacements,
            TransferEvidenceVerified = transfer is not null,
            MissingMaterials = transfer?.Missing.Select(missing => missing.OriginalFileName).ToArray() ?? [],
        };

        void EnsureParents(string path)
        {
            if (path == staging || createdDirectories.ContainsKey(path)) return;
            //CSJ,variable=directory,ディレクトリ
            var directory = Path.GetDirectoryName(path)!;
            EnsureParents(directory);
            //CSJ,variable=parentHandle,親ハンドル
            var parentHandle = directory == staging ? stagingHandle! : createdDirectories[directory];
            //CSJ,variable=created,作成済み
            using var created = OwnedStagingDirectory.CreateAndPin(parentHandle, Path.GetFileName(path));
            //CSJ,variable=identity,識別情報
            var identity = ReadIdentity(created) ?? throw new IOException("展開用フォルダーを識別できません。");
            var handle = subdirectoryFence.Add(path);
            if (ReadIdentity(handle) != identity) throw new IOException("展開用フォルダーが変更されました。");
            directories.Add(new OwnedPath(path, identity));
            createdDirectories.Add(path, handle);
        }

        //CSJ,method=VerifyStagedTree,準備済みツリーを検証する
        void VerifyStagedTree(string actualRoot, CancellationToken verificationToken)
        {
            //CSJ,variable=fileIds,ファイルID一覧
            var fileIds = files.ToDictionary(file => MapFromStaging(file.Path), file => file.Identity, StringComparer.Ordinal);
            //CSJ,variable=directoryIds,ディレクトリID一覧
            var directoryIds = directories.ToDictionary(directory => MapFromStaging(directory.Path), directory => directory.Identity, StringComparer.Ordinal);
            CheckDirectory(actualRoot);
            if (fileIds.Count > 0 || directoryIds.Count > 0)
                throw new IOException("展開した項目が不足しています。");

            string MapFromStaging(string path) => Path.Combine(actualRoot, Path.GetRelativePath(staging, path));

            //CSJ,method=CheckDirectory,ディレクトリを確認する
            void CheckDirectory(string directory)
            {
                //CSJ,variable=fence,整合性境界
                using var fence = DirectoryFence.Open(directory, enforceShare: true);
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    verificationToken.ThrowIfCancellationRequested();
                    if (directoryIds.Remove(child, out var expectedId))
                    {
                        using var handle = OpenOrdinaryDirectory(child);
                        if (expectedId is null || ReadIdentity(handle) != expectedId)
                            throw new IOException("展開したフォルダーが変更されました。");
                        CheckDirectory(child);
                    }
                    else if (!fileIds.Remove(child, out expectedId) || expectedId is null)
                    {
                        throw new IOException("展開先に所有を確認できない項目があります。");
                    }
                    else
                    {
                        using var handle = OpenOrdinaryPath(child, expectDirectory: false, allowRename: false);
                        if (ReadIdentity(handle) != expectedId) throw new IOException("展開ファイルが変更されました。");
                    }
                }
            }
        }

        //CSJ,method=VerifyStagedFilesAsync,準備済みファイル一覧を検証する（非同期）
        async Task VerifyStagedFilesAsync(string actualRoot, CancellationToken verificationToken)
        {
            foreach (var (original, evidence) in verifiedStagedFiles)
            {
                //CSJ,variable=path,パス
                var path = Path.Combine(actualRoot, Path.GetRelativePath(staging, original));
                await using var current = YmmpxTransfer.OpenOrdinaryRead(path);
                if (await ProjectFileEvidence.ReadAsync(current, verificationToken).ConfigureAwait(false) != evidence)
                    throw new IOException("復元したファイルが公開時に変更されました。");
            }
        }

        //CSJ,method=VerifyPublishedSetAsync,確定済みファイル一式を検証する（非同期）
        async Task VerifyPublishedSetAsync()
        {
            // NTFS requires child handles closed for rename. Reopen the published set, holding
            // each verified file and its ordinary parent until every comparison completes.
            using var heldDirectories = new DirectoryFence(enforceShare: true);
            if (transfer is not null)
                //CSJ,variable=directory,ディレクトリ
                foreach (var directory in directories)
                {
                    //CSJ,variable=path,パス
                    var path = Path.Combine(final, Path.GetRelativePath(staging, directory.Path));
                    if (ReadIdentity(heldDirectories.Add(path)) != directory.Identity)
                        throw new IOException("公開後の展開フォルダーが変更されました。");
                }

            var heldFiles = new List<FileStream>(verifiedStagedFiles.Count);
            try
            {
                foreach (var (original, evidence) in verifiedStagedFiles)
                {
                    //CSJ,variable=path,パス
                    var path = Path.Combine(final, Path.GetRelativePath(staging, original));
                    var current = YmmpxTransfer.OpenOrdinaryRead(path);
                    heldFiles.Add(current);
                    if (await ProjectFileEvidence.ReadAsync(current).ConfigureAwait(false) != evidence)
                        throw new IOException("公開後の展開ファイルが変更されました。");
                }
                VerifyStagedTree(final, CancellationToken.None);
            }
            finally
            {
                //CSJ,variable=stream,ストリーム
                foreach (var stream in heldFiles) stream.Dispose();
            }
        }
    }

    //CSJ,method=ReadOwnedOutputEvidenceAsync,管理対象の出力証跡を読み取る（非同期）
    private static async Task<ProjectFileIdentity> ReadOwnedOutputEvidenceAsync(FileStream output, CancellationToken token)
    {
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Position = 0;
        return await ProjectFileEvidence.ReadAsync(output, token).ConfigureAwait(false);
    }

    private static bool ContainsTransferPlaceholder(JsonNode node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return node switch
        {
            JsonObject obj => obj.Any(property => property.Value is not null &&
                ((YmmpxTransfer.IsReferenceProperty(property.Key) && property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var path) &&
                    Normalize(path).StartsWith(YmmpxTransfer.MissingPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    ContainsTransferPlaceholder(property.Value, token))),
            JsonArray array => array.Any(child => child is not null && ContainsTransferPlaceholder(child, token)),
            _ => false,
        };
    }

    //CSJ,method=VerifyTransferAsync,受け渡しを検証する（非同期）
    private static async Task<YmmpxTransfer.Manifest> VerifyTransferAsync(Stream source, Stream manifestInput, string comment,
        //CSJ,parameter=packageName,パッケージ名前
        string packageName, Package package, CancellationToken token)
    {
        if (package.HasLegacyAliases)
            throw new InvalidDataException("受け渡しパッケージに旧形式の素材名マップがあります。");
        //CSJ,variable=manifest,目録
        var manifest = await YmmpxTransfer.ReadManifestAsync(manifestInput, packageName, token).ConfigureAwait(false);
        if (manifest.Version == 2)
        {
            if (package.Project[YmmpxTransfer.EvidenceProperty] is not JsonObject proof ||
                proof["ManifestDigest"]?.GetValue<string>() != YmmpxTransfer.ManifestDigest(manifest.PackageName, manifest.Assets, manifest.Missing) ||
                proof["Materials"] is not JsonArray materials || materials.Count != manifest.Assets.Length)
                throw new InvalidDataException("受け渡し情報がプロジェクトの承認記録と一致しません。");
            for (var i = 0; i < materials.Count; i++)
                if (materials[i]?["FilePath"]?.GetValue<string>() != manifest.Assets[i].ArchivePath)
                    throw new InvalidDataException("同梱素材の対応が承認記録と一致しません。");
            package.Project.Remove(YmmpxTransfer.EvidenceProperty);
        }
        else if (comment != YmmpxTransfer.MakeArchiveComment(manifest.PackageName, manifest.Assets, manifest.Missing))
            throw new InvalidDataException("受け渡し情報のハッシュが YMMPX の記録と一致しません。");
        //CSJ,variable=position,位置
        var position = source.Position;
        source.Position = 0;
        //CSJ,variable=archiveHash,アーカイブハッシュ
        var archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        source.Position = position;
        if (!archiveHash.Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            manifest.Assets.Length != package.Resources.Count)
            throw new InvalidDataException("受け渡し情報と YMMPX の内容が一致しません。");

        //CSJ,variable=references,参照一覧
        var references = new HashSet<string>(StringComparer.Ordinal);
        //CSJ,variable=asset,素材
        foreach (var asset in manifest.Assets)
        {
            token.ThrowIfCancellationRequested();
            if (!package.Resources.Contains(asset.ArchivePath) || package.Files[asset.ArchivePath].Length != asset.Length)
                throw new InvalidDataException("素材名またはサイズが受け渡し情報と一致しません。");
            var entry = package.Files[asset.ArchivePath];
            //CSJ,variable=data,データ
            await using var data = entry.Open();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await CopyEntryAsync(data, Stream.Null, entry.Length, entry.Crc32, token, digest).ConfigureAwait(false);
            if (!Convert.ToHexString(digest.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("素材の SHA-256 が受け渡し情報と一致しません。");
            references.Add(asset.ArchivePath);
        }
        foreach (var item in manifest.Missing) references.Add(item.ReferencePath);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        CheckReferences(package.Project);
        if (!referenced.SetEquals(references))
            throw new InvalidDataException("プロジェクトで参照されていない受け渡し素材があります。");
        return manifest;

        //CSJ,method=CheckReferences,参照一覧を確認する
        void CheckReferences(JsonNode node)
        {
            token.ThrowIfCancellationRequested();
            if (node is JsonObject obj)
            {
                //CSJ,variable=propertyNames,プロパティ名前一覧
                var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (property, value) in obj)
                {
                    if (!propertyNames.Add(property))
                        throw new InvalidDataException("受け渡しプロジェクトに重複した JSON 名があります。");
                    if (YmmpxTransfer.IsReferenceProperty(property) && value is JsonValue pathValue &&
                        pathValue.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
                    {
                        if (!references.Contains(path) || (ReferenceEquals(obj, package.Project) &&
                            property.Equals("FilePath", StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidDataException("許可されていない素材参照がプロジェクトにあります。");
                        referenced.Add(path);
                    }
                    else if (value is not null) CheckReferences(value);
                }
            }
            else if (node is JsonArray array)
                foreach (var child in array) if (child is not null) CheckReferences(child);
        }
    }

    private static ZipArchive OpenValidated(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=archive,アーカイブ
        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            YmmpxLibBridge.ValidateArchiveForExtraction(archive, token);
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    //CSJ,method=ReadPackageAsync,パッケージを読み取る（非同期）
    private static async Task<Package> ReadPackageAsync(ZipArchive archive, CancellationToken token, ProjectSource? expectedProject = null)
    {
        //CSJ,variable=files,ファイル一覧
        var files = archive.Entries.Where(entry => !Normalize(entry.FullName).EndsWith('/'))
            .ToDictionary(entry => Normalize(entry.FullName), StringComparer.Ordinal);
        if (!files.TryGetValue("_ymmpx_project_path.txt", out var marker) || !files.TryGetValue("links.json", out var links))
            throw new InvalidDataException("現行形式のプロジェクト情報がありません。");
        //CSJ,variable=markerBytes,マーカーバイト列
        var markerBytes = await ReadBoundedAsync(marker, 4096, token).ConfigureAwait(false);
        //CSJ,variable=projectName,プロジェクト名
        var projectName = new UTF8Encoding(false, true).GetString(markerBytes).TrimStart('\uFEFF').Trim();
        if (projectName.Contains('/') || projectName.Contains('\\') || !projectName.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            !files.TryGetValue(projectName, out var projectEntry))
            throw new InvalidDataException("パッケージに対象プロジェクトがありません。");
        //CSJ,variable=resources,リソース一覧
        var resources = files.Keys.Where(name => name.StartsWith("resources/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (files.Count != resources.Count + 3)
            throw new InvalidDataException("現行形式で未対応の項目があります。");
        // Current links store the archive-relative path as both key and value.
        // Released v0.3.0 instead stores an exact basename mapped to resources/<same basename>.
        //CSJ,variable=linkBytes,リンクバイト列
        var linkBytes = await ReadBoundedAsync(links, MaxMetadataBytes, token).ConfigureAwait(false);
        //CSJ,variable=document,文書
        using var document = JsonDocument.Parse(WithoutBom(linkBytes));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("素材マップが JSON オブジェクトではありません。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        //CSJ,variable=property,プロパティ
        foreach (var property in document.RootElement.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not string member ||
                !resources.Contains(member) || !seen.Add(member))
                throw new InvalidDataException("素材マップに対応する項目がありません。");
            if (property.Name != member &&
                (!AssetInventory.IsOrdinaryPathComponent(property.Name) || member != "resources/" + property.Name ||
                    !aliases.TryAdd(property.Name, member)))
                throw new InvalidDataException("旧形式の素材名マップが不正です。");
        }
        if (seen.Count != resources.Count || (aliases.Count != 0 && aliases.Count != resources.Count))
            throw new InvalidDataException("素材マップが不足または混在しています。");
        //CSJ,variable=projectBytes,プロジェクトバイト列
        var projectBytes = await ReadBoundedAsync(projectEntry, MaxProjectBytes, token).ConfigureAwait(false);
        //CSJ,variable=projectDocument,プロジェクト文書
        using var projectDocument = JsonDocument.Parse(WithoutBom(projectBytes));
        ValidateProjectJson(projectDocument.RootElement, resources, token);
        if (expectedProject is { } original &&
            !SamePackagedProject(original.Document, projectDocument.RootElement, resources, aliases, original, token))
            throw new InvalidDataException("同梱したプロジェクトの内容が選択したプロジェクトと一致しません。");
        //CSJ,variable=project,プロジェクト
        var project = JsonNode.Parse(WithoutBom(projectBytes).Span) as JsonObject
            ?? throw new InvalidDataException("プロジェクトが JSON オブジェクトではありません。");
        if (aliases.Count > 0) NormalizeLegacyAliases(project, aliases, token);
        return new Package(files, projectName, project, resources, aliases.Count > 0);
    }

    private static void NormalizeLegacyAliases(JsonObject project, IReadOnlyDictionary<string, string> aliases, CancellationToken token)
    {
        var usedAliases = new HashSet<string>(StringComparer.Ordinal);
        Visit(project);
        if (usedAliases.Count != aliases.Count)
            throw new InvalidDataException("旧形式の素材名マップに未使用の項目があります。");

        void Visit(JsonNode node)
        {
            token.ThrowIfCancellationRequested();
            if (node is JsonObject obj)
            {
                //CSJ,variable=hasReference,参照の有無
                var hasReference = false;
                foreach (var (property, value) in obj.ToArray())
                {
                    if (property.Equals("FilePath", StringComparison.OrdinalIgnoreCase))
                    {
                        if (hasReference) throw new InvalidDataException("旧形式のプロジェクトに曖昧な素材参照があります。");
                        hasReference = true;
                        if (value is JsonValue path && path.TryGetValue<string>(out var key) && aliases.TryGetValue(key, out var member))
                        {
                            obj[property] = member;
                            usedAliases.Add(key);
                        }
                    }
                    if (value is not null) Visit(value);
                }
            }
            else if (node is JsonArray array)
                foreach (var child in array) if (child is not null) Visit(child);
        }
    }

    private static bool SamePackagedProject(JsonElement original, JsonElement packaged, HashSet<string> resources,
        IReadOnlyDictionary<string, string> aliases, ProjectSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (original.ValueKind != packaged.ValueKind) return false;

        switch (original.ValueKind)
        {
            case JsonValueKind.Object:
                var remaining = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                //CSJ,variable=property,プロパティ
                foreach (var property in original.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (!remaining.TryAdd(property.Name, property.Value)) return false;
                }
                //CSJ,variable=property,プロパティ
                foreach (var property in packaged.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (!remaining.Remove(property.Name, out var previous)) return false;
                    // Current YMMPX rewrites nonempty FilePath strings to members of its resource map.
                    if (property.Name.Equals("FilePath", StringComparison.OrdinalIgnoreCase) &&
                        previous.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(previous.GetString()) && property.Value.ValueKind == JsonValueKind.String &&
                        (aliases.TryGetValue(property.Value.GetString()!, out var member) ||
                            (TryGetInternalPath(property.Value.GetString()!, out member) && resources.Contains(member))))
                    {
                        // Archive-assigned names do not identify the original media. Bind each
                        // referenced archive member to its source path for the byte check.
                        //CSJ,variable=path,パス
                        var path = PackagingDetector.ResolveMaterialPath(previous.GetString()!, source.Directory);
                        if (path is null || (!source.MaterialPaths.TryAdd(member, path) &&
                            !string.Equals(source.MaterialPaths[member], path, StringComparison.OrdinalIgnoreCase)))
                            return false;
                        continue;
                    }
                    if (!SamePackagedProject(previous, property.Value, resources, aliases, source, token)) return false;
                }
                return remaining.Count == 0;

            case JsonValueKind.Array:
                if (original.GetArrayLength() != packaged.GetArrayLength()) return false;
                using (var after = packaged.EnumerateArray())
                    foreach (var item in original.EnumerateArray())
                    {
                        after.MoveNext();
                        if (!SamePackagedProject(item, after.Current, resources, aliases, source, token)) return false;
                    }
                return true;

            default:
                return JsonElement.DeepEquals(original, packaged);
        }
    }

    //CSJ,method=ValidateProjectJson,プロジェクトJSONを検証する
    private static void ValidateProjectJson(JsonElement element, HashSet<string> resources, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            //CSJ,variable=names,名前一覧
            var names = new HashSet<string>(StringComparer.Ordinal);
            //CSJ,variable=property,プロパティ
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("プロジェクトに重複した JSON 名があります。");
                if (property.Name == "FilePath" && property.Value.ValueKind == JsonValueKind.String &&
                    TryGetInternalPath(property.Value.GetString()!, out var path) &&
                    !resources.Contains(path))
                    throw new InvalidDataException("プロジェクトの同梱素材が不足しています。");
                ValidateProjectJson(property.Value, resources, token);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateProjectJson(child, resources, token);
    }

    private static int RewriteReferences(JsonNode node, HashSet<string> resources, string final, CancellationToken token,
        //CSJ,parameter=transfer,受け渡し
        YmmpxTransfer.Manifest? transfer, bool hasLegacyAliases, IReadOnlyDictionary<string, string> restoredNames)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=count,件数
        var count = 0;
        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj.ToArray())
                if ((transfer is not null ? YmmpxTransfer.IsReferenceProperty(name) :
                    name.Equals("FilePath", hasLegacyAliases ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) &&
                    value is JsonValue pathValue && pathValue.TryGetValue<string>(out var path) &&
                    ((TryGetInternalPath(path, out var internalPath) && resources.Contains(internalPath)) ||
                        (transfer is not null && path.StartsWith(YmmpxTransfer.MissingPrefix, StringComparison.Ordinal))))
                {
                    obj[name] = Path.Combine(final, restoredNames.GetValueOrDefault(internalPath, internalPath).Replace('/', Path.DirectorySeparatorChar));
                    count++;
                }
                else if (value is not null) count += RewriteReferences(value, resources, final, token, transfer, hasLegacyAliases, restoredNames);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null) count += RewriteReferences(child, resources, final, token, transfer, hasLegacyAliases, restoredNames);
        return count;
    }

    //CSJ,method=Normalize,正規化する
    private static string Normalize(string name) => name.Replace('\\', '/');

    //CSJ,method=TryGetInternalPath,内部パスの取得を試みる
    private static bool TryGetInternalPath(string reference, out string path)
    {
        path = Normalize(reference);
        var start = 0;
        while (path.AsSpan(start).StartsWith("./", StringComparison.Ordinal)) start += 2;
        if (start > 0) path = path[start..];
        return path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlyMemory<byte> WithoutBom(byte[] data) => data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })
        ? data.AsMemory(3) : data.AsMemory();

    private static async Task<byte[]> ReadBoundedAsync(ZipArchiveEntry entry, int maximum, CancellationToken token)
    {
        if (entry.Length > maximum) throw new InvalidDataException("パッケージの情報ファイルが大きすぎます。");
        //CSJ,variable=buffer,バッファー
        using var buffer = new MemoryStream(checked((int)entry.Length));
        await using var source = entry.Open();
        await CopyEntryAsync(source, buffer, entry.Length, entry.Crc32, token).ConfigureAwait(false);
        return buffer.ToArray();
    }

    //CSJ,method=CopyEntryAsync,項目をコピーする（非同期）
    private static async Task CopyEntryAsync(Stream input, Stream output, long expectedBytes, uint expectedCrc, CancellationToken token,
        IncrementalHash? digest = null)
    {
        //CSJ,variable=buffer,バッファー
        var buffer = new byte[81920];
        //CSJ,variable=size,サイズ
        long size = 0;
        var crc = uint.MaxValue;
        while (true)
        {
            //CSJ,variable=count,件数
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            size += count;
            if (size > expectedBytes) throw new InvalidDataException("展開サイズが記録と一致しません。");
            for (var i = 0; i < count; i++)
                crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            digest?.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (size != expectedBytes) throw new InvalidDataException("展開サイズが記録と一致しません。");
        if (~crc != expectedCrc) throw new InvalidDataException("ファイルの CRC が記録と一致しません。");
    }

    private static uint[] MakeCrcTable()
    {
        //CSJ,variable=table,表
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xEDB88320U);
            table[i] = crc;
        }
        return table;
    }

    //CSJ,method=RequireOrdinaryDirectory,要求通常ディレクトリ
    private static void RequireOrdinaryDirectory(string path)
    {
        for (var cursor = new DirectoryInfo(path); cursor is not null; cursor = cursor.Parent)
            if ((cursor.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("リンクまたはフォルダー以外のパスは展開先に使用できません。");
    }

    private static void TryCleanup(OwnedPath entry, bool isDirectory)
    {
        if (entry.Identity is null) return;
        try
        {
            RequireOrdinaryDirectory(Path.GetDirectoryName(entry.Path)!);
            using var handle = OpenOrdinaryPath(entry.Path, isDirectory, allowRename: true);
            if (ReadIdentity(handle) == entry.Identity) TryDeleteOpenedPath(handle);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    //CSJ,method=OpenOrdinaryDirectory,通常ディレクトリを開く
    private static SafeFileHandle OpenOrdinaryDirectory(string path, bool allowRename = false)
        => OpenOrdinaryPath(path, expectDirectory: true, allowRename);

    internal static SafeFileHandle OpenPackageForPublication(string path)
        => OpenOrdinaryPath(path, expectDirectory: false, allowRename: true);

    //CSJ,method=CreateOwnedOutputFile,管理対象の出力ファイルを作成する
    internal static FileStream CreateOwnedOutputFile(string path) => OwnedFilePublication.CreateOwnedOutputFile(path);

    //CSJ,method=ReadIdentity,識別情報を読み取る
    private static FileIdentity? ReadIdentity(SafeFileHandle handle)
    {
        //CSJ,variable=fileIdInfo,ファイルID情報
        const int fileIdInfo = 18;
        return GetFileIdentityByHandle(handle, fileIdInfo, out var identity, (uint)Marshal.SizeOf<NativeFileIdentity>()) &&
            identity.Id != Guid.Empty
            ? new FileIdentity(identity.Volume, identity.Id)
            : null;
    }

    internal static void RenameOpenedPath(SafeFileHandle handle, string final) => OwnedFilePublication.RenameOpenedPath(handle, final);

    //CSJ,method=TryDeleteOpenedPath,表示済みのパスの削除を試みる
    internal static bool TryDeleteOpenedPath(SafeFileHandle handle) => OwnedFilePublication.TryDeleteOpenedPath(handle);

    //CSJ,record=FileIdentity,ファイル識別情報
    private readonly record struct FileIdentity(ulong Volume, Guid Id);

    //CSJ,struct=NativeFileIdentity,ネイティブファイル識別情報
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileIdentity
    {
        //CSJ,field=Volume,ボリューム
        internal ulong Volume;
        //CSJ,field=Id,識別ID
        internal Guid Id;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdentityByHandle(SafeFileHandle file, int informationClass, out NativeFileIdentity information, uint length);

}
