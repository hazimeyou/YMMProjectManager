using System;
using System.IO;
using System.Text;

namespace YMMProjectManager.Infrastructure;

public sealed class FileLogger
{
    private readonly string logPath;
    private readonly object sync = new();
    private StreamWriter? writer;

    public FileLogger(string logPath)
    {
        this.logPath = logPath;
        EnsureLogDirectory();
    }

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Error(string message, Exception ex)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");
    }

    public void Error(Exception ex, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");
    }

    public void Flush()
    {
        try
        {
            lock (sync)
            {
                writer?.Flush();
            }
        }
        catch
        {
            // Logging failures should not break plugin behavior.
        }
    }

    private void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
            lock (sync)
            {
                EnsureWriter();
                writer!.Write(line);
            }
        }
        catch
        {
            // Logging failures should not break plugin behavior.
        }
    }

    private void EnsureLogDirectory()
    {
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private void EnsureWriter()
    {
        if (writer is not null)
        {
            return;
        }

        EnsureLogDirectory();
        var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }
}
