using System.Diagnostics;
using System.IO;
using System.Text;

namespace YMMProjectManager.Infrastructure.Packaging;

public sealed class YmmpxLibInstallGuide
{
    public const string YmmpxLibPluginLatestDownloadUrl =
        "https://github.com/hazimeyou/YmmpxLib/releases/latest/download/YmmpxLibPlugin.ymme";

    private readonly FileLogger logger;
    private readonly Action<ProcessStartInfo>? launcher;

    public YmmpxLibInstallGuide(FileLogger logger, Action<ProcessStartInfo>? launcher = null)
    {
        this.logger = logger;
        this.launcher = launcher;
    }

    public string GetPassiveStatusMessage()
    {
        return "YmmpxLibPlugin が未導入です。.ymmpx の同梱・展開を使うには導入が必要です。";
    }

    public string BuildMissingPluginMessage(IReadOnlyList<string> searchedPaths, IReadOnlyList<string> legacyPaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("YmmpxLibPlugin が見つかりません。");
        builder.AppendLine();
        builder.AppendLine(".ymmpx の同梱・展開には YmmpxLibPlugin が必要です。");
        builder.AppendLine();
        builder.AppendLine("導入先:");
        builder.AppendLine("YMM4 フォルダー");
        builder.AppendLine("└ user");
        builder.AppendLine("   └ plugin");
        builder.AppendLine("      └ YmmpxLibPlugin");
        builder.AppendLine();
        builder.AppendLine("入手先:");
        builder.AppendLine("YmmpxLib Releases");
        builder.AppendLine(YmmpxLibPluginLatestDownloadUrl);
        builder.AppendLine();
        builder.AppendLine("導入後は YMM4 を再起動してください。");

        if (legacyPaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("古い YmmpxLib フォルダーが残っている可能性があります。");
            builder.AppendLine("削除後、YMM4 を再起動してください。");
            foreach (var legacyPath in legacyPaths)
            {
                builder.AppendLine($"- {legacyPath}");
            }
        }

        if (searchedPaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("探索パス:");
            foreach (var path in searchedPaths)
            {
                builder.AppendLine($"- {path}");
            }
        }

        builder.AppendLine();
        builder.Append("ダウンロードページを開きますか？");
        return builder.ToString();
    }

    public IReadOnlyList<string> FindLegacyFolders(string? baseDirectory = null)
    {
        var resolvedBaseDirectory = ResolveBaseDirectory(baseDirectory);
        if (string.IsNullOrWhiteSpace(resolvedBaseDirectory))
        {
            return [];
        }

        var results = new List<string>();
        foreach (var ancestor in EnumerateAncestors(resolvedBaseDirectory))
        {
            AddIfExists(results, Path.Combine(ancestor, "user", "plugin", "YMMProjectManager", "YmmpxLib"));
            AddIfExists(results, Path.Combine(ancestor, "user", "plugin", "YmmpxLib"));
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TryOpenDownloadPage()
    {
        try
        {
            logger.Info($"YmmpxLibPlugin のダウンロードページを開きます。url={YmmpxLibPluginLatestDownloadUrl}");
            var startInfo = new ProcessStartInfo
            {
                FileName = YmmpxLibPluginLatestDownloadUrl,
                UseShellExecute = true,
            };
            if (launcher is not null)
            {
                launcher(startInfo);
            }
            else
            {
                Process.Start(startInfo);
            }

            logger.Info("YmmpxLibPlugin のダウンロードページを開きました。");
            logger.Flush();
            return true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"YmmpxLibPlugin のダウンロードページを開けませんでした。url={YmmpxLibPluginLatestDownloadUrl}");
            logger.Flush();
            return false;
        }
    }

    private static void AddIfExists(ICollection<string> results, string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                results.Add(Path.GetFullPath(path));
            }
        }
        catch
        {
        }
    }

    private static IEnumerable<string> EnumerateAncestors(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static string? ResolveBaseDirectory(string? baseDirectory)
    {
        var candidate = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return null;
        }
    }
}
