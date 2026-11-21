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

internal static class YmmpxPackageReader
{
    private const int MaxMetadataBytes = 16 * 1024 * 1024;
    internal const int MaxProjectBytes = 64 * 1024 * 1024;
    internal const string RetainedStagingPath = "YmmpxRetainedStagingPath";
    private static readonly uint[] CrcTable = MakeCrcTable();

    private sealed record Package(Dictionary<string, ZipArchiveEntry> Files, string ProjectName, JsonObject Project, HashSet<string> Resources);
    private sealed record OwnedPath(string Path, FileIdentity? Identity);
    private sealed record ProjectSource(string Directory, JsonElement Document)
    {
        public Dictionary<string, string> MaterialPaths { get; } = new(StringComparer.Ordinal);
    }

    internal static async Task ValidateAsync(Stream source, string projectPath, JsonElement expectedProject, CancellationToken token)
    {
        using var archive = OpenValidated(source, token);
        var projectSource = new ProjectSource(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, expectedProject);
        var package = await ReadPackageAsync(archive, token, projectSource).ConfigureAwait(false);
        if (!string.Equals(package.ProjectName, Path.GetFileName(projectPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("同梱したプロジェクト名が選択したプロジェクトと一致しません。");
        foreach (var resource in package.Resources)
        {
            var entry = package.Files[resource];
            await using var input = entry.Open();
            if (!projectSource.MaterialPaths.TryGetValue(resource, out var materialPath))
            {
                await CopyEntryAsync(input, Stream.Null, entry.Length, entry.Crc32, token).ConfigureAwait(false);
                continue;
            }

            await using var material = new FileStream(materialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            const string mismatch = "同梱した素材が元のファイルと一致しません。";
            if (material.Length != entry.Length) throw new InvalidDataException(mismatch);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await CopyEntryAsync(input, Stream.Null, entry.Length, entry.Crc32, token, digest).ConfigureAwait(false);
            var materialHash = await SHA256.HashDataAsync(material, token).ConfigureAwait(false);
            if (!materialHash.AsSpan().SequenceEqual(digest.GetHashAndReset())) throw new InvalidDataException(mismatch);
        }
    }

    internal static async Task VerifyTransferAsync(Stream source, Stream manifest, string packageName, CancellationToken token)
    {
        using var archive = OpenValidated(source, token);
        var package = await ReadPackageAsync(archive, token).ConfigureAwait(false);
        await VerifyTransferAsync(source, manifest, archive.Comment, packageName, package, token).ConfigureAwait(false);
    }

    internal static async Task<YmmpxExtractResult> ExtractAsync(string packagePath, string destination, CancellationToken token,
        IProgress<double>? progress, bool requireTransfer = false)
    {
        var final = Path.TrimEndingDirectorySeparator(AssetPool.FullOrdinaryPath(Path.GetFullPath(destination)));
        var parent = Path.GetDirectoryName(final);
        if (parent is null || final.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(parent) ||
            new DriveInfo(Path.GetPathRoot(final)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("展開先には、既存のローカル親フォルダーの直下に新しいフォルダーを指定してください。");
        using var parentFence = DirectoryFence.Open(parent);
        if (File.Exists(final) || Directory.Exists(final))
            throw new IOException("展開先が既に存在します。新しいフォルダー名を指定してください。");

        packagePath = Path.GetFullPath(packagePath);
        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        using var packageFence = DirectoryFence.Open(packageDirectory, enforceShare: true);
        await using var source = YmmpxTransfer.OpenOrdinaryRead(packagePath);
        using var archive = OpenValidated(source, token);
        var package = await ReadPackageAsync(archive, token).ConfigureAwait(false);
        var manifestPath = Path.Combine(packageDirectory, YmmpxTransfer.ManifestName);
        var isTransfer = requireTransfer || archive.Comment.StartsWith(YmmpxTransfer.ArchiveCommentPrefix, StringComparison.Ordinal) ||
            package.Resources.Any(path => path.StartsWith(YmmpxTransfer.MediaPrefix, StringComparison.OrdinalIgnoreCase)) ||
            ContainsTransferPlaceholder(package.Project, token);
        await using var manifestInput = isTransfer ? YmmpxTransfer.OpenOrdinaryRead(manifestPath) : null;
        var transfer = manifestInput is not null
            ? await VerifyTransferAsync(source, manifestInput, archive.Comment, Path.GetFileName(packagePath), package, token).ConfigureAwait(false)
            : null;
        var replacements = RewriteReferences(package.Project, package.Resources, final, token, transfer);

        var staging = Path.Combine(parent, ".ymmpx-" + Guid.NewGuid().ToString("N"));
        var files = new List<OwnedPath>();
        var verifiedStagedFiles = new Dictionary<string, ProjectFileIdentity>(StringComparer.Ordinal);
        var directories = new List<OwnedPath>();
        var createdDirectories = new HashSet<string>(StringComparer.Ordinal);
        using var subdirectoryFence = new DirectoryFence(enforceShare: true);
        token.ThrowIfCancellationRequested();
        RequireOrdinaryDirectory(parent);
        ReserveDirectory(staging);
        SafeFileHandle? stagingHandle = null;
        var published = false;
        ProjectFileIdentity? restoredIdentity = null;
        try
        {
            stagingHandle = OpenOrdinaryDirectory(staging, allowRename: true);
            foreach (var (name, entry) in package.Files)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
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
                    var evidence = await ReadOwnedOutputEvidenceAsync(output, token).ConfigureAwait(false);
                    verifiedStagedFiles.Add(target, evidence);
                    if (name == package.ProjectName) restoredIdentity = evidence;
                }
                progress?.Report((double)files.Count / package.Files.Count * 0.99);
            }
            if (manifestInput is not null)
            {
                var target = Path.Combine(staging, YmmpxTransfer.ManifestName);
                await using var manifestOutput = OwnedFilePublication.CreateOwnedOutputFile(target, readBack: true);
                files.Add(new OwnedPath(target, ReadIdentity(manifestOutput.SafeFileHandle)));
                manifestInput.Position = 0;
                await manifestInput.CopyToAsync(manifestOutput, token).ConfigureAwait(false);
                verifiedStagedFiles.Add(target, await ReadOwnedOutputEvidenceAsync(manifestOutput, token).ConfigureAwait(false));
            }
            token.ThrowIfCancellationRequested();
            await VerifyStagedFilesAsync(staging, token).ConfigureAwait(false);
            // Windows may require child directory handles to be closed before renaming.
            // Keep the staging root open and publish that exact object without replacement.
            subdirectoryFence.Dispose();
            token.ThrowIfCancellationRequested();
            var stagingIdentity = ProjectFileEvidence.ReadPhysicalIdentity(stagingHandle);
            RenameOpenedPath(stagingHandle, final);
            published = true;
            stagingHandle.Dispose();
            stagingHandle = null;
            try
            {
                using var publishedFence = DirectoryFence.Open(final, enforceShare: true);
                using var openedFinal = OpenOrdinaryDirectory(final);
                if (ProjectFileEvidence.ReadPhysicalIdentity(openedFinal) != stagingIdentity)
                    throw new IOException("展開したフォルダーが変更されました。");
                await VerifyStagedFilesAsync(final, CancellationToken.None).ConfigureAwait(false);
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
            if (stagingHandle is not null)
            {
                foreach (var path in files) TryCleanup(path, isDirectory: false);
                subdirectoryFence.Dispose();
                foreach (var path in directories.AsEnumerable().Reverse()) TryCleanup(path, isDirectory: true);
                removed = TryDeleteOpenedPath(stagingHandle);
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
            if (path == staging || createdDirectories.Contains(path)) return;
            EnsureParents(Path.GetDirectoryName(path)!);
            RequireOrdinaryDirectory(Path.GetDirectoryName(path)!);
            ReserveDirectory(path);
            var handle = subdirectoryFence.Add(path);
            directories.Add(new OwnedPath(path, ReadIdentity(handle)));
            createdDirectories.Add(path);
        }

        async Task VerifyStagedFilesAsync(string actualRoot, CancellationToken verificationToken)
        {
            // NTFS requires child file handles to close before the folder rename.
            // Reopen by relative name on each side of publication and compare the owned bytes and identity.
            foreach (var (original, evidence) in verifiedStagedFiles)
            {
                var path = Path.Combine(actualRoot, Path.GetRelativePath(staging, original));
                using var fence = actualRoot == staging ? null : DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
                await using var current = YmmpxTransfer.OpenOrdinaryRead(path);
                if (await ProjectFileEvidence.ReadAsync(current, verificationToken).ConfigureAwait(false) != evidence)
                    throw new IOException("復元したファイルが公開時に変更されました。");
            }
        }
    }

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

    private static async Task<YmmpxTransfer.Manifest> VerifyTransferAsync(Stream source, Stream manifestInput, string comment,
        string packageName, Package package, CancellationToken token)
    {
        if (!comment.StartsWith(YmmpxTransfer.ArchiveCommentPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("受け渡し情報に対応した YMMPX が必要です。");
        var manifest = await YmmpxTransfer.ReadManifestAsync(manifestInput, packageName, token).ConfigureAwait(false);
        if (comment != YmmpxTransfer.MakeArchiveComment(manifest.PackageName, manifest.Assets, manifest.Missing))
            throw new InvalidDataException("受け渡し情報のハッシュが YMMPX の記録と一致しません。");
        var position = source.Position;
        source.Position = 0;
        var archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        source.Position = position;
        if (!archiveHash.Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            manifest.Assets.Length != package.Resources.Count)
            throw new InvalidDataException("受け渡し情報と YMMPX の内容が一致しません。");

        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            token.ThrowIfCancellationRequested();
            if (!package.Resources.Contains(asset.ArchivePath) || package.Files[asset.ArchivePath].Length != asset.Length)
                throw new InvalidDataException("素材名またはサイズが受け渡し情報と一致しません。");
            var entry = package.Files[asset.ArchivePath];
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

        void CheckReferences(JsonNode node)
        {
            token.ThrowIfCancellationRequested();
            if (node is JsonObject obj)
            {
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

    private static async Task<Package> ReadPackageAsync(ZipArchive archive, CancellationToken token, ProjectSource? expectedProject = null)
    {
        var files = archive.Entries.Where(entry => !Normalize(entry.FullName).EndsWith('/'))
            .ToDictionary(entry => Normalize(entry.FullName), StringComparer.Ordinal);
        if (!files.TryGetValue("_ymmpx_project_path.txt", out var marker) || !files.TryGetValue("links.json", out var links))
            throw new InvalidDataException("現行形式のプロジェクト情報がありません。");
        var markerBytes = await ReadBoundedAsync(marker, 4096, token).ConfigureAwait(false);
        var projectName = new UTF8Encoding(false, true).GetString(markerBytes).TrimStart('\uFEFF').Trim();
        if (projectName.Contains('/') || projectName.Contains('\\') || !projectName.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase) ||
            !files.TryGetValue(projectName, out var projectEntry))
            throw new InvalidDataException("パッケージに対象プロジェクトがありません。");
        var resources = files.Keys.Where(name => name.StartsWith("resources/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (files.Count != resources.Count + 3)
            throw new InvalidDataException("現行形式で未対応の項目があります。");
        // The current packager records each resource using its archive-relative path
        // as both key and value. Old alias maps require a separate compatibility contract.
        var linkBytes = await ReadBoundedAsync(links, MaxMetadataBytes, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(WithoutBom(linkBytes));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("素材マップが JSON オブジェクトではありません。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (!resources.Contains(property.Name) || !seen.Add(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != property.Name)
                throw new InvalidDataException("素材マップに対応する項目がありません。");
        }
        if (seen.Count != resources.Count) throw new InvalidDataException("素材マップが不足しています。");
        var projectBytes = await ReadBoundedAsync(projectEntry, MaxProjectBytes, token).ConfigureAwait(false);
        using var projectDocument = JsonDocument.Parse(WithoutBom(projectBytes));
        ValidateProjectJson(projectDocument.RootElement, resources, token);
        if (expectedProject is { } original && !SamePackagedProject(original.Document, projectDocument.RootElement, resources, original, token))
            throw new InvalidDataException("同梱したプロジェクトの内容が選択したプロジェクトと一致しません。");
        var project = JsonNode.Parse(WithoutBom(projectBytes).Span) as JsonObject
            ?? throw new InvalidDataException("プロジェクトが JSON オブジェクトではありません。");
        return new Package(files, projectName, project, resources);
    }

    private static bool SamePackagedProject(JsonElement original, JsonElement packaged, HashSet<string> resources,
        ProjectSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (original.ValueKind != packaged.ValueKind) return false;

        switch (original.ValueKind)
        {
            case JsonValueKind.Object:
                var remaining = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in original.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (!remaining.TryAdd(property.Name, property.Value)) return false;
                }
                foreach (var property in packaged.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (!remaining.Remove(property.Name, out var previous)) return false;
                    // Current YMMPX rewrites nonempty FilePath strings to members of its resource map.
                    if (property.Name == "FilePath" && previous.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(previous.GetString()) && property.Value.ValueKind == JsonValueKind.String &&
                        TryGetInternalPath(property.Value.GetString()!, out var member) && resources.Contains(member))
                    {
                        // Archive-assigned names do not identify the original media. Bind each
                        // referenced archive member to its source path for the byte check.
                        var path = PackagingDetector.ResolveMaterialPath(previous.GetString()!, source.Directory);
                        if (path is null || (!source.MaterialPaths.TryAdd(member, path) &&
                            !string.Equals(source.MaterialPaths[member], path, StringComparison.OrdinalIgnoreCase)))
                            return false;
                        continue;
                    }
                    if (!SamePackagedProject(previous, property.Value, resources, source, token)) return false;
                }
                return remaining.Count == 0;

            case JsonValueKind.Array:
                if (original.GetArrayLength() != packaged.GetArrayLength()) return false;
                using (var after = packaged.EnumerateArray())
                    foreach (var item in original.EnumerateArray())
                    {
                        after.MoveNext();
                        if (!SamePackagedProject(item, after.Current, resources, source, token)) return false;
                    }
                return true;

            default:
                return JsonElement.DeepEquals(original, packaged);
        }
    }

    private static void ValidateProjectJson(JsonElement element, HashSet<string> resources, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
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
        YmmpxTransfer.Manifest? transfer = null)
    {
        token.ThrowIfCancellationRequested();
        var count = 0;
        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj.ToArray())
                if ((transfer is not null ? YmmpxTransfer.IsReferenceProperty(name) : name == "FilePath") &&
                    value is JsonValue pathValue && pathValue.TryGetValue<string>(out var path) &&
                    ((TryGetInternalPath(path, out var internalPath) && resources.Contains(internalPath)) ||
                        (transfer is not null && path.StartsWith(YmmpxTransfer.MissingPrefix, StringComparison.Ordinal))))
                {
                    obj[name] = Path.Combine(final, internalPath.Replace('/', Path.DirectorySeparatorChar));
                    count++;
                }
                else if (value is not null) count += RewriteReferences(value, resources, final, token, transfer);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null) count += RewriteReferences(child, resources, final, token, transfer);
        return count;
    }

    private static string Normalize(string name) => name.Replace('\\', '/');

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
        using var buffer = new MemoryStream(checked((int)entry.Length));
        await using var source = entry.Open();
        await CopyEntryAsync(source, buffer, entry.Length, entry.Crc32, token).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task CopyEntryAsync(Stream input, Stream output, long expectedBytes, uint expectedCrc, CancellationToken token,
        IncrementalHash? digest = null)
    {
        var buffer = new byte[81920];
        long size = 0;
        var crc = uint.MaxValue;
        while (true)
        {
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

    private static void ReserveDirectory(string path)
    {
        if (!CreateDirectory(@"\\?\" + path, IntPtr.Zero))
            throw new IOException("展開用フォルダーを確保できません。", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    private static SafeFileHandle OpenOrdinaryDirectory(string path, bool allowRename = false)
        => OpenOrdinaryPath(path, expectDirectory: true, allowRename);

    internal static SafeFileHandle OpenPackageForPublication(string path)
        => OpenOrdinaryPath(path, expectDirectory: false, allowRename: true);

    internal static IDisposable ReserveInstallerDirectory(string tempRoot, out string directory)
    {
        directory = Path.GetFullPath(tempRoot);
        var fence = DirectoryFence.Open(directory);
        try
        {
            foreach (var part in new[] { "YMMProjectManager", "YmmpxLibPlugin" })
            {
                directory = Path.Combine(directory, part);
                if (!CreateDirectory(ExtendedPath(directory), IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 183) throw new IOException("ダウンロード先を作成できません。", new Win32Exception(error));
                }
                fence.Add(directory);
            }
            return fence;
        }
        catch
        {
            fence.Dispose();
            throw;
        }
    }

    internal static FileStream CreateOwnedOutputFile(string path) => OwnedFilePublication.CreateOwnedOutputFile(path);

    private static FileIdentity? ReadIdentity(SafeFileHandle handle)
    {
        const int fileIdInfo = 18;
        return GetFileIdentityByHandle(handle, fileIdInfo, out var identity, (uint)Marshal.SizeOf<NativeFileIdentity>()) &&
            identity.Id != Guid.Empty
            ? new FileIdentity(identity.Volume, identity.Id)
            : null;
    }

    internal static void RenameOpenedPath(SafeFileHandle handle, string final) => OwnedFilePublication.RenameOpenedPath(handle, final);

    internal static bool TryDeleteOpenedPath(SafeFileHandle handle) => OwnedFilePublication.TryDeleteOpenedPath(handle);

    private readonly record struct FileIdentity(ulong Volume, Guid Id);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileIdentity
    {
        internal ulong Volume;
        internal Guid Id;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdentityByHandle(SafeFileHandle file, int informationClass, out NativeFileIdentity information, uint length);

}
