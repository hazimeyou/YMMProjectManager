using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>Budget and cleanup for completed PM captures in the filmstrip cache.</summary>
internal static partial class ThumbnailCacheMaintenance
{
    internal const long ByteLimit = 256L * 1024 * 1024;
    internal static readonly object Sync = new();

    private const string ReceiptName = "ownership.json";
    internal const int MaxReceiptBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal sealed record FrameProof(string Name, ulong Volume, Guid FileId, string Sha256, long Bytes);
    private sealed record Receipt(int Version, ulong Volume, Guid DirectoryId, FrameProof[] Frames);
    private sealed record Capture(string Path, long Order, (ulong, Guid) Identity, (ulong, Guid) Marker,
        (ulong, Guid) ReceiptIdentity, long Bytes);

    internal readonly record struct Usage(long ManagedBytes, int ManagedCount, long UnverifiedBytes, long InterruptedBytes, int Skipped);

    internal static void WriteReceipt(string path, (ulong Volume, Guid FileId) directory, IEnumerable<FrameProof> frames)
    {
        using var receipt = OwnedFilePublication.CreateOwnedOutputFile(Path.Combine(path, ReceiptName));
        JsonSerializer.Serialize(receipt, new Receipt(1, directory.Volume, directory.FileId, frames.OrderBy(x => x.Name).ToArray()), Options);
        receipt.Flush(flushToDisk: true);
    }

    internal static (long Bytes, int Count) Inspect(string root)
    {
        lock (Sync)
        {
            if (!Directory.Exists(root)) return (0, 0);
            using var lease = OwnedFilePublication.DirectoryFence.Open(root, enforceShare: true);
            var captures = ReadCaptures(root).ToArray();
            return (captures.Sum(capture => capture.Bytes), captures.Length);
        }
    }

    // Counting and deleting intentionally have different ownership requirements.
    internal static Usage InspectUsage(string root)
    {
        lock (Sync)
        {
            if (!Directory.Exists(root)) return default;
            using var rootLease = OwnedFilePublication.DirectoryFence.Open(root, enforceShare: true);
            var managed = ReadCaptures(root).ToDictionary(capture => capture.Path, StringComparer.Ordinal);
            long unverified = 0, interrupted = 0;
            var skipped = 0;
            foreach (var project in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(project);
                if (name.Length != 64 || !name.All(Uri.IsHexDigit)) continue;
                try
                {
                    using var projectLease = OwnedFilePublication.DirectoryFence.Open(project, enforceShare: true);
                    unverified += MeasureOrdinaryFiles(project, ref skipped);
                    foreach (var folder in Directory.EnumerateDirectories(project))
                    {
                        if (managed.ContainsKey(folder)) continue;
                        var child = Path.GetFileName(folder);
                        var pending = child.StartsWith(".capture-pending-", StringComparison.Ordinal) &&
                            Guid.TryParseExact(child.AsSpan(".capture-pending-".Length), "N", out _);
                        if (!pending && !ThumbnailCachePublication.TryReadGenerationOrder(child, out _))
                        {
                            skipped++;
                            continue;
                        }
                        try
                        {
                            using var pin = OwnedFilePublication.OpenOrdinaryPath(folder, expectDirectory: true, allowRename: false,
                                FileShare.ReadWrite, enforceDirectoryShare: true);
                            var bytes = MeasureOrdinaryFiles(folder, ref skipped);
                            skipped += Directory.EnumerateDirectories(folder).Count();
                            if (pending || !File.Exists(Path.Combine(folder, "complete"))) interrupted += bytes;
                            else unverified += bytes;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            }
            return new(managed.Values.Sum(capture => capture.Bytes), managed.Count, unverified, interrupted, skipped);
        }
    }

    private static long MeasureOrdinaryFiles(string directory, ref int skipped)
    {
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                using var file = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
                bytes += RandomAccess.GetLength(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return bytes;
    }

    internal static int Trim(string root, long budget, CancellationToken token = default, bool deleteAll = false)
    {
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
        lock (Sync)
        {
            if (!Directory.Exists(root)) return 0;
            using var rootLease = OwnedFilePublication.DirectoryFence.Open(root, enforceShare: true);
            var captures = ReadCaptures(root).OrderBy(capture => capture.Order).ToArray();
            var total = captures.Sum(capture => capture.Bytes);
            var deleted = 0;
            foreach (var capture in captures)
            {
                token.ThrowIfCancellationRequested();
                if (!deleteAll && total <= budget) break;
                if (!TryDelete(capture)) continue;
                total -= capture.Bytes;
                deleted++;
            }
            if (!deleteAll && total > budget)
                throw new IOException("所有を確認できるキャッシュを整理しても空き容量が不足しています。");
            return deleted;
        }
    }

    private static IEnumerable<Capture> ReadCaptures(string root)
    {
        foreach (var project in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(project);
            if (name.Length != 64 || !name.All(Uri.IsHexDigit)) continue;
            string[] captures;
            try
            {
                using var projectLease = OwnedFilePublication.DirectoryFence.Open(project, enforceShare: true);
                captures = Directory.GetDirectories(project, "capture-*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var path in captures)
            {
                name = Path.GetFileName(path);
                if (!ThumbnailCachePublication.TryReadGenerationOrder(name, out var order)) continue;
                var capture = TryRead(path, order);
                if (capture is not null) yield return capture;
            }
        }
    }

    private static Capture? TryRead(string path, long order)
    {
        try
        {
            using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
            using var directory = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: true, allowRename: false,
                FileShare.ReadWrite, enforceDirectoryShare: true);
            var identity = ProjectFileEvidence.ReadPhysicalIdentity(directory);
            using var files = PinExactCapture(path, identity, allowDelete: false);
            return new(path, order, identity, files.Marker, files.ReceiptIdentity, files.Bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static bool TryDelete(Capture capture)
    {
        try
        {
            using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(capture.Path)!, enforceShare: true);
            using var directory = OwnedFilePublication.OpenOrdinaryPath(capture.Path, expectDirectory: true, allowRename: true,
                FileShare.ReadWrite, enforceDirectoryShare: true);
            if (ProjectFileEvidence.ReadPhysicalIdentity(directory) != capture.Identity) return false;
            using (var files = PinExactCapture(capture.Path, capture.Identity, allowDelete: true))
            {
                if (files.Marker != capture.Marker || files.ReceiptIdentity != capture.ReceiptIdentity)
                    return false;
                // The marker is pinned first. Readers will ignore a partially removed capture.
                foreach (var file in files.Handles)
                    if (!OwnedFilePublication.TryDeleteOpenedPath(file)) return false;
            }
            return OwnedFilePublication.TryDeleteOpenedPath(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static CaptureFiles PinExactCapture(string path, (ulong, Guid) identity, bool allowDelete,
        bool verifyContents = false, CancellationToken token = default, long maxFrameBytes = ByteLimit)
    {
        var pins = new CaptureFiles();
        try
        {
            const int knownFiles = ThumbnailCachePublication.SlotCount + 2;
            if (Directory.EnumerateFileSystemEntries(path).Take(knownFiles + 1).Count() != knownFiles)
                throw new IOException("The cache contains unknown files or has no ownership record.");
            var marker = pins.Open(path, "complete", allowDelete);
            if (RandomAccess.GetLength(marker) != 0)
                throw new IOException("The cache marker is invalid.");
            pins.Marker = ProjectFileEvidence.ReadPhysicalIdentity(marker);

            var receiptHandle = pins.Open(path, ReceiptName, allowDelete);
            pins.ReceiptIdentity = ProjectFileEvidence.ReadPhysicalIdentity(receiptHandle);
            var size = RandomAccess.GetLength(receiptHandle);
            if (size is <= 0 or > MaxReceiptBytes)
                throw new IOException("The cache ownership record has an invalid size.");
            var json = new byte[(int)size];
            if (RandomAccess.Read(receiptHandle, json, 0) != json.Length)
                throw new IOException("The cache ownership record is incomplete.");
            var receipt = JsonSerializer.Deserialize<Receipt>(json, Options);
            if (receipt is not { Version: 1, Frames.Length: ThumbnailCachePublication.SlotCount } ||
                (receipt.Volume, receipt.DirectoryId) != identity)
                throw new IOException("The capture directory does not match its ownership record.");
            pins.Bytes = size;
            var shouldHash = allowDelete || verifyContents;
            var block = shouldHash ? new byte[64 * 1024] : null;
            using var hash = shouldHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            for (var slot = 0; slot < ThumbnailCachePublication.SlotCount; slot++)
            {
                token.ThrowIfCancellationRequested();
                var expected = receipt.Frames[slot];
                var name = slot.ToString("D3", CultureInfo.InvariantCulture) + ".png";
                if (expected?.Name != name || expected.Bytes < 0 || expected.Bytes > maxFrameBytes ||
                    expected.Sha256 is not { Length: 64 } digest)
                    throw new IOException("The cache ownership record contains an invalid frame.");
                var frame = pins.Open(path, name, allowDelete);
                if (ProjectFileEvidence.ReadPhysicalIdentity(frame) != (expected.Volume, expected.FileId) ||
                    RandomAccess.GetLength(frame) != expected.Bytes)
                    throw new IOException("The cache frame identification changed.");
                if (hash is not null && !string.Equals(HashFrame(frame, expected.Bytes, block!, hash, token), digest, StringComparison.Ordinal))
                    throw new IOException("The cache frame contents changed.");
                pins.Bytes = checked(pins.Bytes + expected.Bytes);
            }
            return pins;
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    private static string HashFrame(SafeFileHandle frame, long size, byte[] block, IncrementalHash hash, CancellationToken token)
    {
        for (var offset = 0L; offset < size;)
        {
            token.ThrowIfCancellationRequested();
            var length = RandomAccess.Read(frame, block.AsSpan(0, (int)Math.Min(block.Length, size - offset)), offset);
            if (length == 0) throw new IOException("The cache frame is incomplete.");
            hash.AppendData(block.AsSpan(0, length));
            offset += length;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class CaptureFiles : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        internal long Bytes { get; set; }
        internal (ulong, Guid) Marker { get; set; }
        internal (ulong, Guid) ReceiptIdentity { get; set; }

        internal SafeFileHandle Open(string path, string name, bool allowDelete)
        {
            var handle = OwnedFilePublication.OpenOrdinaryPath(Path.Combine(path, name), expectDirectory: false, allowRename: allowDelete);
            Handles.Add(handle);
            return handle;
        }

        public void Dispose()
        {
            foreach (var handle in Handles) handle.Dispose();
        }
    }
}
