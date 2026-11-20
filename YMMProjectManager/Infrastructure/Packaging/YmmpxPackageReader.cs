using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using YMMProjectManager.Domain.Packaging;

namespace YMMProjectManager.Infrastructure.Packaging;

internal static class YmmpxPackageReader
{
    private const int MaxMetadataBytes = 16 * 1024 * 1024;
    internal const int MaxProjectBytes = 64 * 1024 * 1024;
    private static readonly uint[] CrcTable = MakeCrcTable();

    private sealed record Package(Dictionary<string, ZipArchiveEntry> Files, string ProjectName, JsonObject Project, HashSet<string> Resources);

    internal static async Task ValidateAsync(Stream source, CancellationToken token)
    {
        using var archive = OpenValidated(source, token);
        var package = await ReadPackageAsync(archive, token).ConfigureAwait(false);
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
        var files = new List<string>();
        var directories = new List<string>();
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
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                files.Add(target);
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
        catch
        {
            if (stagingHandle is not null)
            {
                foreach (var path in files) TryCleanup(path, isDirectory: false);
                subdirectoryFence.Dispose();
                foreach (var path in directories.AsEnumerable().Reverse()) TryCleanup(path, isDirectory: true);
                TryDeleteOpenedPath(stagingHandle);
            }
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
            directories.Add(path);
            subdirectoryFence.Add(path);
            createdDirectories.Add(path);
        }
    }

    private static ZipArchive OpenValidated(Stream stream, CancellationToken token)
    {
        YmmpxLibBridge.ValidateArchiveForExtraction(stream, token);
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    }

    private static async Task<Package> ReadPackageAsync(ZipArchive archive, CancellationToken token)
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
        var project = JsonNode.Parse(WithoutBom(projectBytes).Span) as JsonObject
            ?? throw new InvalidDataException("プロジェクトが JSON オブジェクトではありません。");
        return new Package(files, projectName, project, resources);
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

    private static void TryCleanup(string path, bool isDirectory)
    {
        try
        {
            RequireOrdinaryDirectory(Path.GetDirectoryName(path)!);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            if (isDirectory) Directory.Delete(path, recursive: false);
            else File.Delete(path);
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

    private sealed class DirectoryFence : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];

        public static DirectoryFence Open(string path)
        {
            var ancestors = new Stack<string>();
            for (var cursor = new DirectoryInfo(path); cursor is not null; cursor = cursor.Parent)
                ancestors.Push(cursor.FullName);
            var fence = new DirectoryFence();
            try
            {
                while (ancestors.TryPop(out var directory)) fence.Add(directory);
                return fence;
            }
            catch
            {
                fence.Dispose();
                throw;
            }
        }

        public void Add(string path)
        {
            handles.Add(OpenOrdinaryDirectory(path));
        }

        public void Dispose()
        {
            for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
            handles.Clear();
        }
    }

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

    internal static FileStream CreateOwnedDownload(string path)
    {
        const uint genericWrite = 0x40000000;
        const uint delete = 0x10000;
        const uint createNew = 1;
        const uint openWithoutFollowingLink = 0x00200000;
        var handle = OpenPath(ExtendedPath(path), genericWrite | delete, FileShare.None, IntPtr.Zero,
            createNew, openWithoutFollowingLink, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            try { return new FileStream(handle, FileAccess.Write, bufferSize: 1); }
            catch
            {
                TryDeleteOpenedPath(handle);
                handle.Dispose();
                throw;
            }
        }
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("一時ダウンロードを作成できません。", new Win32Exception(error));
    }

    private static SafeFileHandle OpenOrdinaryPath(string path, bool expectDirectory, bool allowRename)
    {
        const uint readAttributes = 0x80;
        const uint genericRead = 0x80000000;
        const uint delete = 0x10000;
        const uint openExisting = 3;
        const uint openDirectoryWithoutFollowingLink = 0x02200000;
        const int fileAttributeTagInfo = 9;
        // Deny writable and delete handles while using a directory as an output boundary.
        var access = (expectDirectory ? readAttributes : genericRead) | (allowRename ? delete : 0);
        var handle = OpenPath(ExtendedPath(path), access, FileShare.Read,
            IntPtr.Zero, openExisting, openDirectoryWithoutFollowingLink, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("保存先を保護できません。", new Win32Exception(error));
        }
        if (!GetFileInformationByHandleEx(handle, fileAttributeTagInfo, out var info, 8) ||
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != (expectDirectory ? FileAttributes.Directory : 0))
        {
            handle.Dispose();
            throw new IOException("通常のファイルまたはフォルダーを使用してください。リンクは利用できません。");
        }
        return handle;
    }

    private static string ExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    internal static void RenameOpenedPath(SafeFileHandle handle, string final)
    {
        const int fileRenameInfo = 3;
        // FILE_RENAME_INFO has a native-sized RootDirectory and a trailing UTF-16 name.
        // A null RootDirectory uses the complete destination path. ReplaceIfExists is false.
        var name = Encoding.Unicode.GetBytes(ExtendedPath(final));
        var nameLengthOffset = 2 * IntPtr.Size;
        var nameOffset = nameLengthOffset + sizeof(int);
        var data = new byte[nameOffset + name.Length + sizeof(char)];
        BitConverter.TryWriteBytes(data.AsSpan(nameLengthOffset), name.Length);
        name.CopyTo(data, nameOffset);
        if (!SetFileInformationByHandle(handle, fileRenameInfo, data, (uint)data.Length))
            throw new IOException("新しい名前で保存できません。", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    internal static void TryDeleteOpenedPath(SafeFileHandle handle)
    {
        const int fileDispositionInfo = 4;
        // This marks only the opened file or empty directory for deletion.
        _ = SetFileInformationByHandle(handle, fileDispositionInfo, [1], 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInfo information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint length);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenPath(string path, uint access, FileShare share, IntPtr securityAttributes,
        uint disposition, uint flags, IntPtr template);
}
