using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>Keeps each completed strip in an immutable directory and retains the legacy strip.</summary>
internal sealed class ThumbnailCachePublication : IDisposable
{
    internal const int SlotCount = 64;
    private const string PublishedPrefix = "capture-";
    private const string CompletionName = "complete";
    private readonly string cacheDirectory;
    private readonly OwnedFilePublication.DirectoryFence fence;
    private readonly OwnedStagingDirectory stage;
    private readonly SafeFileHandle stagePin;
    private readonly Dictionary<string, FrameEvidence> ownedFiles = [];
    private bool published;

    internal ThumbnailCachePublication(string cacheDirectory)
    {
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        fence = OpenCacheDirectory(this.cacheDirectory);
        try
        {
            stage = OwnedStagingDirectory.Reserve(this.cacheDirectory, ".capture-pending-");
            try { stagePin = stage.PinRecoveryPath(); }
            catch
            {
                stage.Dispose();
                throw;
            }
        }
        catch
        {
            fence.Dispose();
            throw;
        }
    }

    internal void WriteFrame(int slot, Action<Stream> encode)
    {
        var name = FrameName(slot);
        using var stream = OwnedFilePublication.CreateOwnedOutputFile(Path.Combine(stage.Path, name), readBack: true);
        FrameEvidence evidence;
        try { evidence = new FrameEvidence(ProjectFileEvidence.ReadPhysicalIdentity(stream), null); }
        catch
        {
            OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
            throw;
        }
        ownedFiles.Add(name, evidence);
        encode(stream);
        stream.Flush();
        stream.Position = 0;
        ownedFiles[name] = evidence with { Bytes = stream.Length, Sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
    }

    internal string Publish(CancellationToken token, Action validate)
    {
        lock (ThumbnailCacheMaintenance.Sync) return PublishWithinBudget(token, validate);
    }

    private string PublishWithinBudget(CancellationToken token, Action validate)
    {
        using var verify = new OwnedFilePublication.DirectoryFence(enforceShare: true);
        var ownedDirectory = ProjectFileEvidence.ReadPhysicalIdentity(stagePin);
        long bytes;
        using (var frames = PinVerifiedFiles(stage.Path)) bytes = frames.Bytes;
        var reservedBytes = checked(bytes + ThumbnailCacheMaintenance.MaxReceiptBytes);
        if (reservedBytes > ThumbnailCacheMaintenance.ByteLimit)
            throw new IOException("サムネイル一式がキャッシュ容量上限を超えています。");
        ThumbnailCacheMaintenance.Trim(Path.GetDirectoryName(cacheDirectory)!, ThumbnailCacheMaintenance.ByteLimit - reservedBytes, token);
        var ordinal = Math.Max(DateTime.UtcNow.Ticks, LatestGeneration(cacheDirectory).Ordinal + 1);
        if (ordinal > DateTime.MaxValue.Ticks)
            throw new IOException("サムネイルキャッシュの順序を確定できません。");
        var destination = Path.Combine(cacheDirectory,
            PublishedPrefix + ordinal.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        validate();
        stagePin.Dispose(); // The staging directory's separate identity lease guards the rename.
        stage.Publish(destination, token);
        published = true;

        var finalHandle = verify.Add(destination);
        if (ProjectFileEvidence.ReadPhysicalIdentity(finalHandle) != ownedDirectory)
            throw new IOException("公開したサムネイルの保存先を確認できません。");
        using var verifiedFrames = PinVerifiedFiles(destination);
        validate();
        token.ThrowIfCancellationRequested();
        ThumbnailCacheMaintenance.WriteReceipt(destination, ownedDirectory, ownedFiles.Select(frame =>
            new ThumbnailCacheMaintenance.FrameProof(frame.Key, frame.Value.Identity.Volume, frame.Value.Identity.FileId,
                frame.Value.Sha256!, frame.Value.Bytes)));
        token.ThrowIfCancellationRequested();
        // Readers ignore a directory until the complete set has been verified after the rename.
        using var complete = OwnedFilePublication.CreateOwnedOutputFile(Path.Combine(destination, CompletionName));
        return destination;
    }

    internal static string? GetDisplayDirectory(string cacheRoot, string projectPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(projectPath);
            using var parent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(fullPath)!, enforceShare: true);
            using var file = OwnedFilePublication.OpenOrdinaryPath(fullPath, expectDirectory: false, allowRename: false);
            using var root = OwnedFilePublication.DirectoryFence.Open(cacheRoot, enforceShare: true);
            var cacheDirectory = Path.Combine(cacheRoot, FilmstripCacheKeyFactory.CreateLiveHash(fullPath, file));
            if (TryOpenCacheChild(cacheDirectory, root))
            {
                var generation = LatestGeneration(cacheDirectory).Path;
                if (generation is not null)
                    return generation;
            }

            var legacy = FilmstripCacheKeyFactory.TryCreateHash(fullPath);
            if (legacy is null)
                return null;
            var legacyDirectory = Path.Combine(cacheRoot, legacy);
            return TryOpenCacheChild(legacyDirectory, root) ? legacyDirectory : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool TryOpenCacheChild(string directory, OwnedFilePublication.DirectoryFence root)
    {
        try { root.Add(directory); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static (string? Path, long Ordinal) LatestGeneration(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
            return (null, 0);
        foreach (var path in Directory.EnumerateDirectories(cacheDirectory, PublishedPrefix + "*")
            .OrderDescending(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.Length == PublishedPrefix.Length + 19 + 1 + 32 &&
                name[PublishedPrefix.Length + 19] == '-' &&
                long.TryParse(name.AsSpan(PublishedPrefix.Length, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) &&
                ordinal > 0 && ordinal <= DateTime.MaxValue.Ticks &&
                Guid.TryParseExact(name.AsSpan(PublishedPrefix.Length + 20), "N", out _) &&
                IsComplete(path))
                return (path, ordinal);
        }
        return (null, 0);
    }

    private static bool IsComplete(string path)
    {
        try
        {
            using var folder = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: true, allowRename: false,
                FileShare.ReadWrite, enforceDirectoryShare: true);
            using var marker = OwnedFilePublication.OpenOrdinaryPath(Path.Combine(path, CompletionName),
                expectDirectory: false, allowRename: false);
            for (var slot = 0; slot < SlotCount; slot++)
            {
                var attributes = File.GetAttributes(Path.Combine(path, FrameName(slot)));
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string FrameName(int slot) => slot.ToString("D3", CultureInfo.InvariantCulture) + ".png";

    private static OwnedFilePublication.DirectoryFence OpenCacheDirectory(string path)
    {
        var missing = new Stack<string>();
        var existing = new DirectoryInfo(path);
        while (!existing.Exists)
        {
            missing.Push(existing.FullName);
            existing = existing.Parent ?? throw new IOException("サムネイルの保存先を確認できません。");
        }

        var result = OwnedFilePublication.DirectoryFence.Open(existing.FullName, enforceShare: true);
        try
        {
            while (missing.TryPop(out var directory))
            {
                Directory.CreateDirectory(directory);
                result.Add(directory);
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private VerifiedFrames PinVerifiedFiles(string directory)
    {
        if (ownedFiles.Count != SlotCount || Directory.EnumerateFileSystemEntries(directory).Take(SlotCount + 1).Count() != SlotCount)
            throw new IOException("サムネイル一式の内容が変更されました。");

        var frames = new VerifiedFrames();
        try
        {
            foreach (var (name, evidence) in ownedFiles)
            {
                var file = frames.Open(Path.Combine(directory, name));
                if (ProjectFileEvidence.ReadPhysicalIdentity(file.SafeFileHandle) != evidence.Identity ||
                    Convert.ToHexString(SHA256.HashData(file)) != evidence.Sha256)
                    throw new IOException("一時サムネイルが差し替えられました。");
            }
            return frames;
        }
        catch
        {
            frames.Dispose();
            throw;
        }
    }

    private sealed class VerifiedFrames : IDisposable
    {
        private readonly List<FileStream> streams = [];
        internal long Bytes => streams.Sum(stream => stream.Length);

        internal FileStream Open(string path)
        {
            var file = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
            try
            {
                var stream = new FileStream(file, FileAccess.Read);
                streams.Add(stream);
                return stream;
            }
            catch { file.Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }


    public void Dispose()
    {
        try
        {
            if (!published)
            {
                using (stage.PinRecoveryPath())
                    foreach (var (name, evidence) in ownedFiles)
                    {
                        using var file = OwnedFilePublication.OpenOrdinaryPath(Path.Combine(stage.Path, name),
                            expectDirectory: false, allowRename: true, share: FileShare.Read | FileShare.Delete);
                        if (ProjectFileEvidence.ReadPhysicalIdentity(file) == evidence.Identity)
                            OwnedFilePublication.TryDeleteOpenedPath(file);
                    }
                stagePin.Dispose();
                stage.DeleteIfEmpty();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            stagePin.Dispose();
            stage.Dispose();
            fence.Dispose();
        }
    }
    private sealed record FrameEvidence((ulong Volume, Guid FileId) Identity, string? Sha256, long Bytes = 0);
}
