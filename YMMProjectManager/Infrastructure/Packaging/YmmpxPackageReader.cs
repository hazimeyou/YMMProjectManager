using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
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

    internal static async Task ValidateAsync(Stream source, string expectedProjectName, JsonElement expectedProject, CancellationToken token)
    {
        using var archive = OpenValidated(source, token);
        var package = await ReadPackageAsync(archive, token, expectedProject).ConfigureAwait(false);
        if (!string.Equals(package.ProjectName, expectedProjectName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("同梱したプロジェクト名が選択したプロジェクトと一致しません。");
        foreach (var resource in package.Resources)
        {
            var entry = package.Files[resource];
            await using var input = entry.Open();
            await CopyEntryAsync(input, Stream.Null, entry.Length, entry.Crc32, token).ConfigureAwait(false);
        }
    }

    internal static async Task<YmmpxExtractResult> ExtractAsync(string packagePath, string destination, CancellationToken token, IProgress<double>? progress)
    {
        var final = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var parent = Path.GetDirectoryName(final);
        if (parent is null || final.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(parent) ||
            new DriveInfo(Path.GetPathRoot(final)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("展開先には、既存のローカル親フォルダーの直下に新しいフォルダーを指定してください。");
        using var parentFence = DirectoryFence.Open(parent);
        if (File.Exists(final) || Directory.Exists(final))
            throw new IOException("展開先が既に存在します。新しいフォルダー名を指定してください。");

        await using var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var archive = OpenValidated(source, token);
        var package = await ReadPackageAsync(archive, token).ConfigureAwait(false);
        var replacements = RewriteReferences(package.Project, package.Resources, final, token);

        var staging = Path.Combine(parent, ".ymmpx-" + Guid.NewGuid().ToString("N"));
        var files = new List<OwnedPath>();
        var directories = new List<OwnedPath>();
        var createdDirectories = new HashSet<string>(StringComparer.Ordinal);
        using var subdirectoryFence = new DirectoryFence();
        token.ThrowIfCancellationRequested();
        RequireOrdinaryDirectory(parent);
        ReserveDirectory(staging);
        SafeFileHandle? stagingHandle = null;
        try
        {
            stagingHandle = OpenOrdinaryDirectory(staging, allowRename: true);
            foreach (var (name, entry) in package.Files)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                EnsureParents(Path.GetDirectoryName(target)!);
                RequireOrdinaryDirectory(Path.GetDirectoryName(target)!);
                await using var output = CreateOwnedOutputFile(target);
                files.Add(new OwnedPath(target, ReadIdentity(output.SafeFileHandle)));
                if (name == package.ProjectName)
                    await JsonSerializer.SerializeAsync(output, package.Project, cancellationToken: token).ConfigureAwait(false);
                else
                {
                    await using var input = entry.Open();
                    await CopyEntryAsync(input, output, entry.Length, entry.Crc32, token).ConfigureAwait(false);
                }
                progress?.Report((double)files.Count / package.Files.Count * 0.99);
            }
            token.ThrowIfCancellationRequested();
            // Windows may require child directory handles to be closed before renaming.
            // Keep the staging root open and publish that exact object without replacement.
            subdirectoryFence.Dispose();
            RenameOpenedPath(stagingHandle, final);
        }
        catch (Exception ex)
        {
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
        finally { stagingHandle?.Dispose(); }

        return new YmmpxExtractResult
        {
            Success = true,
            OutputDirectory = final,
            RestoredProjectPath = Path.Combine(final, package.ProjectName),
            ExtractedResourceCount = package.Resources.Count,
            ReplacedPathCount = replacements,
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

    private static async Task<Package> ReadPackageAsync(ZipArchive archive, CancellationToken token, JsonElement? expectedProject = null)
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
        if (expectedProject is { } original && !SamePackagedProject(original, projectDocument.RootElement, resources, token))
            throw new InvalidDataException("同梱したプロジェクトの内容が選択したプロジェクトと一致しません。");
        var project = JsonNode.Parse(WithoutBom(projectBytes).Span) as JsonObject
            ?? throw new InvalidDataException("プロジェクトが JSON オブジェクトではありません。");
        return new Package(files, projectName, project, resources);
    }

    private static bool SamePackagedProject(JsonElement original, JsonElement packaged, HashSet<string> resources, CancellationToken token)
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
                        continue;
                    if (!SamePackagedProject(previous, property.Value, resources, token)) return false;
                }
                return remaining.Count == 0;

            case JsonValueKind.Array:
                if (original.GetArrayLength() != packaged.GetArrayLength()) return false;
                using (var after = packaged.EnumerateArray())
                    foreach (var item in original.EnumerateArray())
                    {
                        after.MoveNext();
                        if (!SamePackagedProject(item, after.Current, resources, token)) return false;
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

    private static int RewriteReferences(JsonNode node, HashSet<string> resources, string final, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var count = 0;
        if (node is JsonObject obj)
        {
            if (obj["FilePath"] is JsonValue value && value.TryGetValue<string>(out var path) &&
                TryGetInternalPath(path, out var internalPath) && resources.Contains(internalPath))
            {
                obj["FilePath"] = Path.Combine(final, internalPath.Replace('/', Path.DirectorySeparatorChar));
                count++;
            }
            foreach (var property in obj)
                if (property.Value is not null) count += RewriteReferences(property.Value, resources, final, token);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null) count += RewriteReferences(child, resources, final, token);
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

    private static async Task CopyEntryAsync(Stream input, Stream output, long expectedBytes, uint expectedCrc, CancellationToken token)
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
