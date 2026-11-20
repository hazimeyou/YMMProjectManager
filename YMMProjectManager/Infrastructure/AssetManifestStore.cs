using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

public sealed record AssetManifest(
    int SchemaVersion,
    Guid ProjectId,
    string ProjectFileName,
    Guid RevisionId,
    Guid? ParentRevisionId,
    DateTimeOffset VerifiedAtUtc,
    IReadOnlyList<AssetMetadata> Assets);

/// <summary>Writes immutable project-associated revisions; existing metadata is never replaced.</summary>
public static class AssetManifestStore
{
    public static AssetManifest Create(string projectPath, IReadOnlyList<AssetMetadata> assets, DateTimeOffset verifiedAtUtc, AssetManifest? parent = null, bool confirmedProjectAssociation = false, bool confirmedHashChanges = false, Guid? projectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(assets);
        var name = Path.GetFileName(projectPath);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Invalid project path.", nameof(projectPath));
        if (projectId == Guid.Empty) throw new InvalidDataException("Project identity is empty.");
        if (parent is not null)
        {
            Validate(parent);
            if (projectId is Guid id && parent.ProjectId != id)
                throw new InvalidDataException("Metadata belongs to a different project identity.");
            if (projectId is null && !string.Equals(parent.ProjectFileName, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Metadata belongs to a different project name. Explicit reassociation is required.");
            if (!confirmedProjectAssociation)
                throw new InvalidDataException("The project association must be confirmed explicitly.");
        }
        var merged = parent?.Assets.ToList() ?? [];
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            AssetInventory.ValidateMetadata(asset);
            var normalized = Path.GetFullPath(asset.RelativePath, Path.GetTempPath());
            if (!requested.Add(normalized)) throw new InvalidDataException("Duplicate asset path.");
            var index = merged.FindIndex(existing => string.Equals(Path.GetFullPath(existing.RelativePath, Path.GetTempPath()), normalized, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && (merged[index].Size != asset.Size || !string.Equals(merged[index].Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase)) && !confirmedHashChanges)
                throw new InvalidDataException("Asset size or hash differs from the previous revision and requires explicit confirmation.");
            if (index < 0) merged.Add(asset); else merged[index] = asset;
        }
        var manifest = new AssetManifest(1, parent?.ProjectId ?? projectId ?? Guid.NewGuid(), name, Guid.NewGuid(),
            parent?.RevisionId, parent?.VerifiedAtUtc ?? verifiedAtUtc, merged);
        Validate(manifest);
        return manifest;
    }

    public static IReadOnlyList<string> FindChangedPaths(AssetManifest parent, IReadOnlyList<AssetMetadata> assets)
    {
        Validate(parent);
        ArgumentNullException.ThrowIfNull(assets);
        var changes = new List<string>();
        foreach (var asset in assets)
        {
            AssetInventory.ValidateMetadata(asset);
            var path = Path.GetFullPath(asset.RelativePath, Path.GetTempPath());
            var existing = parent.Assets.FirstOrDefault(item => string.Equals(Path.GetFullPath(item.RelativePath, Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && (existing.Size != asset.Size || !string.Equals(existing.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase)))
                changes.Add(asset.RelativePath);
        }
        return changes;
    }

    public static async Task<AssetManifest> LoadAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Asset metadata is too large.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return Parse(bytes);
    }

    internal static AssetManifest Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Asset metadata is too large.");
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var manifest = JsonSerializer.Deserialize<AssetManifest>(bytes, options)
            ?? throw new InvalidDataException("Asset metadata is empty.");
        Validate(manifest);
        return manifest;
    }

    public static async Task SaveNewAsync(string path, AssetManifest manifest, CancellationToken token = default)
    {
        Validate(manifest);
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Asset metadata is too large.");
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory);
        var temporary = Path.Combine(directory, $".asset-manifest-pending-{Guid.NewGuid():N}.tmp");
        await using var stream = OwnedFilePublication.CreateOwnedOutputFile(temporary);
        var published = false;
        try
        {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(stream.SafeFileHandle, destination);
            published = true;
        }
        finally
        {
            if (!published) OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
        }
    }

    private static void Validate(AssetManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.ProjectId == Guid.Empty || manifest.RevisionId == Guid.Empty ||
            manifest.ParentRevisionId == Guid.Empty || manifest.ParentRevisionId == manifest.RevisionId ||
            string.IsNullOrWhiteSpace(manifest.ProjectFileName) || Path.GetFileName(manifest.ProjectFileName) != manifest.ProjectFileName ||
            manifest.VerifiedAtUtc == default || manifest.Assets is null)
            throw new InvalidDataException("Invalid or unsupported project asset metadata.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets)
        {
            AssetInventory.ValidateMetadata(asset);
            var normalized = Path.GetFullPath(asset.RelativePath, Path.GetTempPath());
            if (!paths.Add(normalized)) throw new InvalidDataException("Duplicate asset path.");
        }
    }
}
