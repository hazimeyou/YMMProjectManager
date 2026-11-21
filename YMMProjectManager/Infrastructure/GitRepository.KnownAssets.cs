using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YMMProjectManager.Infrastructure.Packaging;

namespace YMMProjectManager.Infrastructure.Git;

public sealed partial class GitRepository
{
    private const string KnownAssetDirectory = "ymmpm-known-assets";
    private const int MaxKnownAssets = 5_000;
    private sealed record KnownAsset(string Name, string Sha256);

    private FileStream OpenAssetRegistryLease() => new(Path.Combine(directory, ".git", "ymmpm-asset-publication.lock"),
        FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);

    internal async Task<bool> ConfirmCommittedAssetReferenceAsync(string commit, string projectPath, string reference, CancellationToken token)
    {
        if (!HasDirectRepository() || !IsObjectId(commit)) return false;
        var relative = Path.GetRelativePath(directory, Path.GetFullPath(projectPath)).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ||
            HasHiddenText(relative) || !relative.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase))
            return false;
        var blob = await InspectPublicationGitAsync(["cat-file", "blob", commit + ":" + relative], token).ConfigureAwait(false);
        if (!blob.Success || blob.StandardError.Length != 0) return false;
        var original = PackagingDetector.NormalizeFilePathKey(reference);
        return PackagingDetector.FindFilePaths(blob.StandardOutput).Any(path =>
            string.Equals(PackagingDetector.NormalizeFilePathKey(path), original, StringComparison.Ordinal));
    }

    internal async Task RememberPublicationAssetAsync(string name, string sha256, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasDirectRepository() || !OnFixedLocalNtfs(directory) || !AssetInventory.IsOrdinaryPathComponent(name) ||
            sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Cannot safely record this asset in Git publication metadata.");
        var git = Path.Combine(directory, ".git");
        using var gitFence = OwnedFilePublication.DirectoryFence.Open(git, enforceShare: true);
        using var registry = OpenAssetRegistryLease();
        var store = Path.Combine(git, KnownAssetDirectory);
        Directory.CreateDirectory(store);
        using var fence = OwnedFilePublication.DirectoryFence.Open(store, enforceShare: true);
        var record = new KnownAsset(name, sha256.ToUpperInvariant());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        var receipt = Path.Combine(store, Convert.ToHexString(SHA256.HashData(bytes)) + ".json");
        if (HasGitEntry(receipt))
        {
            await ConfirmRecordAsync();
            return;
        }
        var temporary = Path.Combine(git, $".ymmpm-asset-record-{Guid.NewGuid():N}.tmp");
        await using var staged = OwnedFilePublication.CreateOwnedOutputFile(temporary);
        var published = false;
        try
        {
            await staged.WriteAsync(bytes, token).ConfigureAwait(false);
            await staged.FlushAsync(token).ConfigureAwait(false);
            staged.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(staged.SafeFileHandle, receipt);
            published = true;
        }
        catch (IOException) when (HasGitEntry(receipt)) { await ConfirmRecordAsync(); }
        finally { if (!published) OwnedFilePublication.TryDeleteOpenedPath(staged.SafeFileHandle); }

        async Task ConfirmRecordAsync()
        {
            if (await ReadKnownAssetAsync(receipt, token).ConfigureAwait(false) != record)
                throw new InvalidDataException("A conflicting Git asset record is present.");
        }
    }

    private async Task<GitResult?> ReadKnownPublicationAssetsAsync(CancellationToken token)
    {
        var git = Path.Combine(directory, ".git");
        var store = Path.Combine(git, KnownAssetDirectory);
        try
        {
            if (!HasGitEntry(store)) return null;
            using var fence = OwnedFilePublication.DirectoryFence.Open(store, enforceShare: true);
            var total = 0;
            foreach (var file in Directory.EnumerateFileSystemEntries(store))
            {
                token.ThrowIfCancellationRequested();
                if (++total > MaxKnownAssets) return PublicationRefusal("Too many known assets to inspect safely.");
                var asset = await ReadKnownAssetAsync(file, token).ConfigureAwait(false);
                SetPublicationAssets([asset.Name], [asset.Sha256]);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return PublicationRefusal("The repository's known asset records cannot be verified.");
        }
    }

    private static async Task<KnownAsset> ReadKnownAssetAsync(string path, CancellationToken token)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length != 64 || !name.All(Uri.IsHexDigit) || !Path.GetExtension(path).Equals(".json", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected file in the Git asset registry.");
        using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
        await using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length is <= 0 or > 4096)
            throw new InvalidDataException("Invalid Git asset record size.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != name)
            throw new InvalidDataException("Git asset record contents were changed.");
        var asset = JsonSerializer.Deserialize<KnownAsset>(bytes);
        if (asset is null || asset.Name is null || !AssetInventory.IsOrdinaryPathComponent(asset.Name) ||
            asset.Sha256 is not { Length: 64 } || !asset.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid Git asset record.");
        return asset;
    }
}
