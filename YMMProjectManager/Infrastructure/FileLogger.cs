using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=FileLogger,ファイルログ出力
public sealed class FileLogger
{
    internal const long DefaultByteLimit = 1024 * 1024;
    internal const int DefaultArchiveLimit = 7;
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    private const int MaxOwnershipBytes = 512;
    private sealed record ArchiveReceipt(int Version, ulong Volume, Guid FileId, long Bytes, string Sha256);
    private sealed record Archive(string Path, DateTime Timestamp, ArchiveReceipt Receipt, (ulong, Guid) OwnershipIdentity);

    private readonly string logPath;
    private readonly object sync = new();
    private readonly TimeProvider time;
    private readonly long byteLimit;
    private bool closed;
    private DateTimeOffset nextMaintenance;

    public FileLogger(string logPath) : this(logPath, TimeProvider.System, DefaultByteLimit) { }

    internal FileLogger(string logPath, TimeProvider time, long byteLimit)
    {
        this.logPath = Path.GetFullPath(logPath);
        this.time = time;
        this.byteLimit = byteLimit;
    }

    internal string LogPath => logPath;

    // Callers use the live message in their UI where appropriate. The persisted log records
    // only a compiler-supplied operation name and typed diagnostics.
    public void Info(string message, [CallerMemberName] string operation = "") =>
        Write("INFO", operation, null);

    public void Error(string message, Exception ex, [CallerMemberName] string operation = "") =>
        Write("ERROR", operation, ex);

    public void Error(Exception ex, string message, [CallerMemberName] string operation = "") =>
        Write("ERROR", operation, ex);

    public void Flush()
    {
        // Every record uses a short-lived UTF-8 append stream.
    }

    internal void Close()
    {
        lock (sync) closed = true;
    }

    private void Write(string level, string operation, Exception? error)
    {
        try
        {
            lock (sync)
            {
                if (closed) return;
                var now = time.GetUtcNow();
                var operationName = operation.Length is > 0 and <= 128 &&
                    operation.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')
                    ? operation : "operation";
                var diagnostic = error is null ? "" : $" {error.GetType().Name} (0x{error.HResult:X8})";
                var line = Encoding.UTF8.GetBytes($"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {operationName}{diagnostic}{Environment.NewLine}");
                if (line.Length > byteLimit) return;

                var directory = Path.GetDirectoryName(logPath)!;
                if (!Directory.Exists(directory))
                {
                    using var parent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(directory)!, enforceShare: true);
                    Directory.CreateDirectory(directory);
                }
                using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
                (ulong, Guid)? rotateIdentity = null;
                try
                {
                    using var pin = OwnedFilePublication.OpenOrdinaryPath(logPath, expectDirectory: false,
                        allowRename: false, FileShare.ReadWrite);
                    using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                    if (stream.Length + line.Length > byteLimit ||
                        File.GetLastWriteTimeUtc(pin) < now.UtcDateTime - DefaultRetention)
                    {
                        rotateIdentity = ProjectFileEvidence.ReadPhysicalIdentity(pin);
                    }
                    else
                    {
                        stream.Seek(0, SeekOrigin.End);
                        stream.Write(line);
                    }
                }
                catch (IOException) when (!File.Exists(logPath))
                {
                    using var output = OwnedFilePublication.CreateOwnedOutputFile(logPath);
                    output.Write(line);
                }
                if (rotateIdentity is { } expected)
                {
                    using (var previous = OwnedFilePublication.OpenOrdinaryPath(logPath, expectDirectory: false, allowRename: true))
                    {
                        if (ProjectFileEvidence.ReadPhysicalIdentity(previous) != expected) return;
                        var stamp = now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                        var archive = logPath + $".{stamp}-{Guid.NewGuid():N}";
                        using var oldLog = new FileStream(previous, FileAccess.Read);
                        if (oldLog.Length > DefaultByteLimit * DefaultArchiveLimit)
                        {
                            // Old versions could produce unbounded logs. Park them without a cleanup receipt.
                            OwnedFilePublication.RenameOpenedPath(previous, archive);
                        }
                        else
                        {
                            using var receipt = OwnedFilePublication.CreateOwnedOutputFile(archive + ".owner");
                            try
                            {
                                var seal = new ArchiveReceipt(1, expected.Item1, expected.Item2, oldLog.Length,
                                    Convert.ToHexString(SHA256.HashData(oldLog)));
                                JsonSerializer.Serialize(receipt, seal);
                                receipt.Flush(flushToDisk: true);
                                OwnedFilePublication.RenameOpenedPath(previous, archive);
                            }
                            catch
                            {
                                OwnedFilePublication.TryDeleteOpenedPath(receipt.SafeFileHandle);
                                throw;
                            }
                        }
                    }
                    using var output = OwnedFilePublication.CreateOwnedOutputFile(logPath);
                    output.Write(line);
                }
                if (rotateIdentity is not null || now >= nextMaintenance)
                {
                    PruneArchives(directory, now.UtcDateTime, deleteAll: false);
                    nextMaintenance = now.AddHours(1);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Logging failures should not break plugin behavior.
        }
    }

    internal (long Bytes, int Archives) InspectLogs()
    {
        lock (sync)
        {
            var directory = Path.GetDirectoryName(logPath)!;
            if (!Directory.Exists(directory)) return (0, 0);
            using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
            var archives = GetArchives(directory).ToArray();
            var active = new FileInfo(logPath);
            var activeBytes = active.Exists && (active.Attributes & FileAttributes.ReparsePoint) == 0 ? active.Length : 0;
            return (activeBytes + archives.Sum(x => x.Receipt.Bytes), archives.Length);
        }
    }

    internal int DeleteArchives()
    {
        lock (sync)
        {
            var directory = Path.GetDirectoryName(logPath)!;
            if (!Directory.Exists(directory)) return 0;
            using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
            return PruneArchives(directory, time.GetUtcNow().UtcDateTime, deleteAll: true);
        }
    }

    private int PruneArchives(string directory, DateTime now, bool deleteAll)
    {
        var liveBytes = 0L;
        var liveCount = 0;
        var deleted = 0;
        foreach (var archive in GetArchives(directory).OrderByDescending(x => x.Timestamp))
        {
            if (!deleteAll && archive.Timestamp >= now - DefaultRetention && liveCount < DefaultArchiveLimit &&
                archive.Receipt.Bytes <= (DefaultArchiveLimit * byteLimit) - liveBytes)
            {
                liveCount++;
                liveBytes += archive.Receipt.Bytes;
                continue;
            }
            if (TryDeleteArchive(archive)) deleted++;
        }
        return deleted;
    }

    private IEnumerable<Archive> GetArchives(string directory)
    {
        var prefix = Path.GetFileName(logPath) + ".";
        foreach (var entry in new DirectoryInfo(directory).EnumerateFiles(prefix + "*"))
        {
            if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var suffix = entry.Name.AsSpan(prefix.Length);
            if (suffix.Length == 47 && suffix[14] == '-' &&
                DateTime.TryParseExact(suffix[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) &&
                Guid.TryParseExact(suffix[15..], "N", out _) &&
                ReadArchive(entry.FullName, timestamp) is { } archive)
                yield return archive;
        }
    }

    private static Archive? ReadArchive(string path, DateTime stamp)
    {
        try
        {
            using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
            using var ownership = OwnedFilePublication.OpenOrdinaryPath(path + ".owner", expectDirectory: false, allowRename: false);
            var receipt = ReadReceipt(ownership);
            return MatchesArchive(handle, receipt)
                ? new Archive(path, stamp, receipt, ProjectFileEvidence.ReadPhysicalIdentity(ownership))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static bool TryDeleteArchive(Archive archive)
    {
        try
        {
            using var handle = OwnedFilePublication.OpenOrdinaryPath(archive.Path, expectDirectory: false, allowRename: true);
            using var ownership = OwnedFilePublication.OpenOrdinaryPath(archive.Path + ".owner", expectDirectory: false, allowRename: true);
            if (ProjectFileEvidence.ReadPhysicalIdentity(ownership) != archive.OwnershipIdentity ||
                ReadReceipt(ownership) != archive.Receipt || !MatchesArchive(handle, archive.Receipt))
                return false;

            using var file = new FileStream(handle, FileAccess.Read);
            if (Convert.ToHexString(SHA256.HashData(file)) != archive.Receipt.Sha256 ||
                !OwnedFilePublication.TryDeleteOpenedPath(handle))
                return false;
            OwnedFilePublication.TryDeleteOpenedPath(ownership);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static ArchiveReceipt ReadReceipt(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);
        if (length is <= 0 or > MaxOwnershipBytes) throw new IOException("Invalid log archive ownership record.");
        Span<byte> bytes = stackalloc byte[(int)length];
        if (RandomAccess.Read(handle, bytes, 0) != bytes.Length) throw new IOException("Incomplete log archive ownership record.");
        return JsonSerializer.Deserialize<ArchiveReceipt>(bytes) is { Version: 1, Bytes: >= 0 and <= DefaultByteLimit * DefaultArchiveLimit,
            Sha256.Length: 64 } seal ? seal : throw new IOException("Invalid log archive ownership record.");
    }

    private static bool MatchesArchive(Microsoft.Win32.SafeHandles.SafeFileHandle handle, ArchiveReceipt seal) =>
        ProjectFileEvidence.ReadPhysicalIdentity(handle) == (seal.Volume, seal.FileId) &&
        RandomAccess.GetLength(handle) == seal.Bytes;
}
