using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMMProjectManager.Infrastructure;

public sealed record AssetMetadata(string RelativePath, long Size, string Sha256, string? DownloadUrl);
public enum AssetVerification { Valid, Missing, SizeMismatch, HashMismatch }

/// <summary>Inspects explicitly selected local assets; it never modifies media files.</summary>
public static class AssetInventory
{
    public static async Task<AssetMetadata> InspectAsync(string projectDirectory, string relativePath, string? downloadUrl = null, CancellationToken token = default)
    {
        var path = Resolve(projectDirectory, relativePath);
        ValidateUrl(downloadUrl);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var size = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        if (stream.Length != size) throw new IOException("Asset changed while being read.");
        return new AssetMetadata(relativePath, size, Convert.ToHexString(hash), downloadUrl);
    }

    public static async Task<AssetVerification> VerifyAsync(string projectDirectory, AssetMetadata asset, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var path = Resolve(projectDirectory, asset.RelativePath);
        ValidateUrl(asset.DownloadUrl);
        if (asset.Size < 0 || asset.Sha256 is null || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid asset metadata.");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (stream.Length != asset.Size) return AssetVerification.SizeMismatch;
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(hash), asset.Sha256, StringComparison.OrdinalIgnoreCase) ? AssetVerification.Valid : AssetVerification.HashMismatch;
        }
        catch (FileNotFoundException) { return AssetVerification.Missing; }
        catch (DirectoryNotFoundException) { return AssetVerification.Missing; }
    }

    // Export is deliberately create-only: an existing manifest, including a corrupt one, is never overwritten.
    public static async Task ExportAsync(string path, IReadOnlyCollection<AssetMetadata> assets, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        token.ThrowIfCancellationRequested();
        foreach (var asset in assets) ValidateMetadata(asset);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(assets, new JsonSerializerOptions { WriteIndented = true });
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                created = true;
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath);
            created = false;
        }
        finally { if (created) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    public static async Task<IReadOnlyList<AssetMetadata>> LoadAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Asset metadata is too large.");
        var assets = await JsonSerializer.DeserializeAsync<List<AssetMetadata>>(stream, cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Asset metadata is empty.");
        foreach (var asset in assets) ValidateMetadata(asset);
        return assets;
    }

    internal static void ValidateMetadata(AssetMetadata asset)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.RelativePath) || Path.IsPathRooted(asset.RelativePath) ||
            asset.RelativePath.Split('/', '\\').Any(part => part == "..") || asset.Size < 0 ||
            asset.Sha256 is null || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid asset metadata.");
        ValidateUrl(asset.DownloadUrl);
    }
    private static string Resolve(string projectDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("Asset path must be relative.", nameof(relativePath));
        var root = Path.GetFullPath(projectDirectory);
        var full = Path.GetFullPath(relativePath, root);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Asset path escapes the project directory.", nameof(relativePath));
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Project root cannot be a reparse point.");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Asset paths through reparse points are not supported.");
        }
        return full;
    }

    private static void ValidateUrl(string? value)
    {
        if (value is null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
            throw new ArgumentException("Asset URL must be HTTPS without credentials, query, or fragment.", nameof(value));
    }
}
