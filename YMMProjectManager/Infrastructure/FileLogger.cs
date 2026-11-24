using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=FileLogger,ファイルログ出力
public sealed class FileLogger
{
    internal const long DefaultByteLimit = 1024 * 1024;
    internal const int DefaultArchiveLimit = 7;
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

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
                        var archive = logPath + $".{now.UtcDateTime:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                        OwnedFilePublication.RenameOpenedPath(previous, archive);
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
            return (activeBytes + archives.Sum(x => x.Length), archives.Length);
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
        foreach (var (path, timestamp, length, identity) in GetArchives(directory).OrderByDescending(x => x.Timestamp))
        {
            if (!deleteAll && timestamp >= now - DefaultRetention && liveCount < DefaultArchiveLimit &&
                length <= (DefaultArchiveLimit * byteLimit) - liveBytes)
            {
                liveCount++;
                liveBytes += length;
                continue;
            }
            try
            {
                using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: true);
                if (ProjectFileEvidence.ReadPhysicalIdentity(handle) == identity &&
                    OwnedFilePublication.TryDeleteOpenedPath(handle)) deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return deleted;
    }

    private IEnumerable<(string Path, DateTime Timestamp, long Length, (ulong, Guid) Identity)> GetArchives(string directory)
    {
        var prefix = Path.GetFileName(logPath) + ".";
        foreach (var entry in new DirectoryInfo(directory).EnumerateFiles())
        {
            if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var suffix = entry.Name.AsSpan(prefix.Length);
            if (suffix.Length == 47 && suffix[14] == '-' &&
                DateTime.TryParseExact(suffix[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) &&
                Guid.TryParseExact(suffix[15..], "N", out _) && ReadArchive(entry.FullName) is { } file)
                yield return (entry.FullName, timestamp, file.Length, file.Identity);
        }
    }

    private static (long Length, (ulong, Guid) Identity)? ReadArchive(string path)
    {
        try
        {
            using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
            return (RandomAccess.GetLength(handle), ProjectFileEvidence.ReadPhysicalIdentity(handle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
