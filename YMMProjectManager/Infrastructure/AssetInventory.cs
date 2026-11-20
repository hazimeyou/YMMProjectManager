using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure;

public sealed record ExternalAssetLocation(string StorageName, string RelativePath);
public sealed record AssetMetadata(string RelativePath, long Size, string Sha256, string? DownloadUrl)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExternalAssetLocation? ExternalLocation { get; init; }
}
public enum AssetVerification { Valid, Missing, SizeMismatch, HashMismatch, ExternalUnverified }
public enum AssetReferenceStatus { Present, Missing, Changed, External, Unavailable }
public sealed record AssetReference(string Path, AssetReferenceStatus Status, AssetMetadata? Actual, AssetMetadata? Expected);

/// <summary>Inspects explicitly selected local assets; it never modifies media files.</summary>
public static class AssetInventory
{
    public static async Task<IReadOnlyList<AssetReference>> ScanProjectAsync(string projectPath,
        IReadOnlyList<AssetMetadata>? expected = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var project = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(project) ?? throw new InvalidDataException("The project has no containing directory.");
        var safeProject = ResolveRelativePath(directory, Path.GetFileName(project));
        await using var stream = new FileStream(safeProject, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("The project is too large for inventory scanning.");
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        var expectedByPath = new Dictionary<string, AssetMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in expected ?? [])
        {
            ValidateMetadata(asset);
            if (!expectedByPath.TryAdd(Path.GetFullPath(asset.RelativePath, directory), asset))
                throw new InvalidDataException("Duplicate expected asset path.");
        }

        using var projectJson = JsonDocument.Parse(json);
        if (projectJson.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The project does not contain a JSON object.");
        var paths = PackagingDetector.FindFilePaths(json);
        if (paths.Count > 1000) throw new InvalidDataException("The project has too many asset references for one inventory scan.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<AssetReference>();
        var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        foreach (var reference in paths)
        {
            token.ThrowIfCancellationRequested();
            var path = PackagingDetector.ResolveMaterialPath(reference, directory);
            if (!seen.Add(path ?? reference)) continue;
            if (path is null)
            {
                results.Add(new AssetReference(reference, AssetReferenceStatus.Unavailable, null, null));
                continue;
            }
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new AssetReference(path, AssetReferenceStatus.External, null, null));
                continue;
            }
            var relative = Path.GetRelativePath(directory, path);
            expectedByPath.TryGetValue(path, out var prior);
            if (prior?.ExternalLocation is not null)
            {
                results.Add(new AssetReference(relative, AssetReferenceStatus.External, null, prior));
                continue;
            }
            try
            {
                var actual = await InspectAsync(directory, relative, prior?.DownloadUrl, token).ConfigureAwait(false);
                var changed = prior is not null && (actual.Size != prior.Size ||
                    !string.Equals(actual.Sha256, prior.Sha256, StringComparison.OrdinalIgnoreCase));
                results.Add(new AssetReference(relative, changed ? AssetReferenceStatus.Changed : AssetReferenceStatus.Present, actual, prior));
            }
            catch (FileNotFoundException) { results.Add(new AssetReference(relative, AssetReferenceStatus.Missing, null, prior)); }
            catch (DirectoryNotFoundException) { results.Add(new AssetReference(relative, AssetReferenceStatus.Missing, null, prior)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                results.Add(new AssetReference(relative, AssetReferenceStatus.Unavailable, null, prior));
            }
        }
        return results;
    }

    public static async Task<AssetMetadata> InspectAsync(string projectDirectory, string relativePath, string? downloadUrl = null, CancellationToken token = default)
    {
        var path = ResolveRelativePath(projectDirectory, relativePath);
        ValidateUrl(downloadUrl);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var size = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        if (stream.Length != size) throw new IOException("Asset changed while being read.");
        return new AssetMetadata(relativePath, size, Convert.ToHexString(hash), downloadUrl);
    }

    public static async Task<AssetVerification> VerifyAsync(string projectDirectory, AssetMetadata asset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateMetadata(asset);
        if (asset.ExternalLocation is not null) return AssetVerification.ExternalUnverified;
        return await VerifyFileAsync(ResolveRelativePath(projectDirectory, asset.RelativePath), asset, token).ConfigureAwait(false);
    }

    public static async Task<AssetMetadata> InspectExternalAsync(string approvedStorageRoot, string projectRelativePath,
        ExternalAssetLocation location, string? downloadUrl = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateExternalLocation(location);
        var asset = await InspectAsync(approvedStorageRoot, location.RelativePath, downloadUrl, token).ConfigureAwait(false);
        var result = asset with { RelativePath = projectRelativePath, ExternalLocation = location };
        ValidateMetadata(result);
        return result;
    }

    public static async Task<AssetVerification> VerifyExternalAsync(string approvedStorageRoot, AssetMetadata asset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateMetadata(asset);
        if (asset.ExternalLocation is not { } location) throw new InvalidDataException("No external storage location is recorded.");
        return await VerifyFileAsync(ResolveRelativePath(approvedStorageRoot, location.RelativePath), asset, token).ConfigureAwait(false);
    }

    private static async Task<AssetVerification> VerifyFileAsync(string path, AssetMetadata asset, CancellationToken token)
    {
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
        if (asset.ExternalLocation is { } location) ValidateExternalLocation(location);
    }

    private static void ValidateExternalLocation(ExternalAssetLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.StorageName) || location.StorageName.Length > 128 ||
            location.StorageName.Any(character => char.IsControl(character) || character is '/' or '\\' or ':') ||
            string.IsNullOrWhiteSpace(location.RelativePath) || Path.IsPathRooted(location.RelativePath) ||
            location.RelativePath.Split('/', '\\').Any(part => part is "" or "." or ".." ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid external asset location.");
    }
    internal static string ResolveRelativePath(string projectDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("Asset path must be relative.", nameof(relativePath));
        var root = Path.GetFullPath(projectDirectory);
        var full = Path.GetFullPath(relativePath, root);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Asset path escapes the project directory.", nameof(relativePath));
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Project path cannot traverse a reparse point.");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            var stem = segment.Split('.')[0].TrimEnd();
            if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && "0123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Asset path contains an invalid or reserved name.", nameof(relativePath));
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
