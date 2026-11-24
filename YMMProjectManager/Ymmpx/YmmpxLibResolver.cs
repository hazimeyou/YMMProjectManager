using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using YukkuriMovieMaker.Commons;

namespace YMMProjectManager.Infrastructure.Packaging;

internal enum YmmpxLibStatus
{
    NotInstalled,
    Unsupported,
    Invalid,
    LoadFailed,
    Ready,
}

//CSJ,class=YmmpxLibResolver,YMMPXライブラリ解決処理
public static class YmmpxLibResolver
{
    private const string LibraryFileName = "YmmpxLib.dll";
    private const string LibraryAssemblyName = "YmmpxLib";
    private const string PluginFileName = "YmmpxLibPlugin.dll";
    private const int MaximumLibraryBytes = 8 * 1024 * 1024;
    private static readonly Version SupportedVersion = new(1, 1, 0, 0);

    // The exact DLL from the official YmmpxLibPlugin v1.1.0 release asset.
    private const string SupportedLibrarySha256 = "9D6E0A5F1A6FE096098CE77F2FD7A970A83A5C7F0476844E441F003AAA10FE23";
    private const string SupportedPluginSha256 = "8918C7B5141BFB27C62444B41C5899985D83CD250B06914F398AF78395D47CE7";
    private static readonly object AssemblyLoadGate = new();
    private static Assembly? verifiedAssembly;

    public static IReadOnlyList<string> GetSearchPaths(string? baseDirectory = null, string? explicitAssemblyPath = null)
    {
        var configured = NormalizePath(GetConfiguredPath(explicitAssemblyPath));
        var installed = GetInstalledPath(baseDirectory);
        if (configured is null) return installed is null ? [] : [installed];
        if (installed is null || string.Equals(configured, installed, StringComparison.OrdinalIgnoreCase))
            return [configured];
        return [configured, installed];
    }

    public static bool TryResolveAssembly(out Assembly? assembly, out string? assemblyPath,
        out IReadOnlyList<string> searchedPaths, string? baseDirectory = null, string? explicitAssemblyPath = null)
    {
        var status = Resolve(out assembly, out assemblyPath, out searchedPaths, baseDirectory, explicitAssemblyPath);
        return status == YmmpxLibStatus.Ready;
    }

    internal static YmmpxLibStatus Resolve(out Assembly? assembly, out string? assemblyPath,
        out IReadOnlyList<string> searchedPaths, string? baseDirectory, string? explicitAssemblyPath)
    {
        var configured = GetConfiguredPath(explicitAssemblyPath);
        var candidate = configured is null ? GetInstalledPath(baseDirectory) : NormalizePath(configured);
        assembly = null;
        assemblyPath = null;
        searchedPaths = candidate is null ? [] : [candidate];
        if (candidate is null) return YmmpxLibStatus.Invalid;

        try
        {
            var attributes = File.GetAttributes(candidate);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return YmmpxLibStatus.Invalid;

            var directory = Path.GetDirectoryName(candidate)!;
            using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
            // Inspect and authenticate the same owned bytes that will be passed to the runtime.
            var bytes = ReadImage(candidate);
            using var verifiedImage = new MemoryStream(bytes, writable: false);
            using (var pe = new PEReader(verifiedImage, PEStreamOptions.LeaveOpen))
            {
                if (!pe.HasMetadata) return YmmpxLibStatus.Invalid;
                var metadata = pe.GetMetadataReader();
                if (!metadata.IsAssembly) return YmmpxLibStatus.Invalid;
                var identity = metadata.GetAssemblyDefinition();
                if (metadata.GetString(identity.Name) != LibraryAssemblyName)
                    return YmmpxLibStatus.Invalid;
                if (identity.Version != SupportedVersion)
                    return YmmpxLibStatus.Unsupported;
            }

            if (!MatchesSha256(bytes, SupportedLibrarySha256))
                return YmmpxLibStatus.Invalid;
            if (configured is null)
            {
                // A loose core DLL in the installed path does not prove the plugin was installed.
                try
                {
                    if (!MatchesSha256(ReadImage(Path.Combine(directory, PluginFileName)), SupportedPluginSha256))
                        return YmmpxLibStatus.Invalid;
                }
                catch (IOException ex) when (IsMissing(ex))
                {
                    return YmmpxLibStatus.Invalid;
                }
            }

            verifiedImage.Position = 0;
            lock (AssemblyLoadGate)
            {
                // Avoid binding to another plug-in's already-loaded assembly or installing a global resolve hook.
                if (verifiedAssembly is null)
                {
                    var context = new AssemblyLoadContext("YMMProjectManager.YmmpxLib", isCollectible: true);
                    try { verifiedAssembly = context.LoadFromStream(verifiedImage); }
                    catch
                    {
                        context.Unload();
                        throw;
                    }
                }
                assembly = verifiedAssembly;
            }
            assemblyPath = candidate;
            return YmmpxLibStatus.Ready;
        }
        catch (IOException ex) when (IsMissing(ex))
        {
            return YmmpxLibStatus.NotInstalled;
        }
        catch (BadImageFormatException)
        {
            return YmmpxLibStatus.Invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return YmmpxLibStatus.LoadFailed;
        }
    }

    private static byte[] ReadImage(string path)
    {
        using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
        using var input = new FileStream(handle, FileAccess.Read);
        if (input.Length is <= 0 or > MaximumLibraryBytes)
            throw new BadImageFormatException("The YmmpxLib image size is invalid.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static bool MatchesSha256(byte[] image, string sha256) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(image)), sha256, StringComparison.Ordinal);

    private static bool IsMissing(IOException ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException ||
        ex.InnerException is Win32Exception { NativeErrorCode: 2 or 3 };

    private static string? GetConfiguredPath(string? explicitAssemblyPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitAssemblyPath)) return explicitAssemblyPath;
        var configured = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static string? GetInstalledPath(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            // Use YMM4's own directory contract, including custom user-directory configurations.
            if (Assembly.GetEntryAssembly()?.GetName().Name == "YukkuriMovieMaker")
            {
                var installed = NormalizePath(AppDirectories.PluginDirectory);
                return installed is null ? null : Path.Combine(installed, "YmmpxLibPlugin", LibraryFileName);
            }
            var moduleDirectory = Path.GetDirectoryName(typeof(YmmpxLibResolver).Assembly.Location);
            baseDirectory = FindPluginDirectory(moduleDirectory) ?? AppContext.BaseDirectory;
        }
        var root = NormalizePath(baseDirectory);
        if (root is null) return null;
        var pluginDirectory = FindPluginDirectory(root) ?? Path.Combine(root, "user", "plugin");
        return Path.Combine(pluginDirectory, "YmmpxLibPlugin", LibraryFileName);
    }

    private static string? FindPluginDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Name.Equals("plugin", StringComparison.OrdinalIgnoreCase) &&
                current.Parent?.Name.Equals("user", StringComparison.OrdinalIgnoreCase) == true)
                return current.FullName;
        }
        return null;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var candidate = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(candidate)) return null;
        try { return Path.GetFullPath(candidate); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }
}
