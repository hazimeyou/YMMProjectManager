using System;
using System.IO;
using System.Text;

namespace YMMProjectManager.Infrastructure;

public sealed class FileLogger
{
    //CSJ,field=logPath,ログパス
    private readonly string logPath;
    //CSJ,field=sync,同期
    private readonly object sync = new();
    //CSJ,field=writer,書き込み処理
    private StreamWriter? writer;

    public FileLogger(string logPath)
    {
        this.logPath = logPath;
        EnsureLogDirectory();
    }

    //CSJ,method=Info,情報
    public void Info(string message)
    {
        Write("INFO", message);
    }

    //CSJ,method=Error,エラー
    public void Error(string message, Exception ex)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");
    }

    //CSJ,method=Error,エラー
    public void Error(Exception ex, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");
    }

    //CSJ,method=Flush,書き出す
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

    //CSJ,method=Write,書き込む
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

    //CSJ,method=EnsureLogDirectory,ログディレクトリを確認する
    private void EnsureLogDirectory()
    {
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    //CSJ,method=EnsureWriter,書き込み処理を確認する
    private void EnsureWriter()
    {
        if (writer is not null)
        {
            return;
        }

        EnsureLogDirectory();
        //CSJ,variable=stream,ストリーム
        var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }
}
