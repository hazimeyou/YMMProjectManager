using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace YMMProjectManager.Infrastructure.Packaging;

public sealed class YmmpxLibInstallGuide
{
    public const string YmmpxLibPluginLatestDownloadUrl =
        "https://github.com/hazimeyou/YmmpxLib/releases/latest/download/YmmpxLibPlugin.ymme";

    private readonly FileLogger logger;
    private readonly Action<ProcessStartInfo>? launcher;
    private readonly Func<string, string, CancellationToken, Task>? downloader;

    public YmmpxLibInstallGuide(
        FileLogger logger,
        Action<ProcessStartInfo>? launcher = null,
        Func<string, string, CancellationToken, Task>? downloader = null)
    {
        this.logger = logger;
        this.launcher = launcher;
        this.downloader = downloader;
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
        builder.Append("YmmpxLibPlugin をダウンロードして起動しますか？");
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

    public async Task<YmmpxLibInstallResult> DownloadAndLaunchInstallerAsync(CancellationToken cancellationToken = default)
    {
        var downloadPath = GetInstallerDownloadPath();
        try
        {
            logger.Info($"YmmpxLibPlugin のダウンロードを開始します。url={YmmpxLibPluginLatestDownloadUrl}");
            logger.Info($"YmmpxLibPlugin のダウンロード先は {downloadPath} です。");
            logger.Flush();

            var downloadDirectory = Path.GetDirectoryName(downloadPath);
            if (!string.IsNullOrWhiteSpace(downloadDirectory))
            {
                Directory.CreateDirectory(downloadDirectory);
            }

            if (File.Exists(downloadPath))
            {
                File.Delete(downloadPath);
            }

            if (downloader is not null)
            {
                await downloader(YmmpxLibPluginLatestDownloadUrl, downloadPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DownloadFileAsync(YmmpxLibPluginLatestDownloadUrl, downloadPath, cancellationToken).ConfigureAwait(false);
            }

            logger.Info($"YmmpxLibPlugin のダウンロードに成功しました。path={downloadPath}");
            logger.Flush();

            var startInfo = new ProcessStartInfo
            {
                FileName = downloadPath,
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

            logger.Info($"YmmpxLibPlugin の .ymme 起動に成功しました。path={downloadPath}");
            logger.Flush();
            return new YmmpxLibInstallResult
            {
                Success = true,
                DownloadPath = downloadPath,
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"YmmpxLibPlugin のダウンロードまたは .ymme 起動に失敗しました。url={YmmpxLibPluginLatestDownloadUrl}, path={downloadPath}");
            logger.Flush();
            return new YmmpxLibInstallResult
            {
                Success = false,
                DownloadPath = downloadPath,
                ErrorMessage = "YmmpxLibPlugin のダウンロードまたは起動に失敗しました。",
            };
        }
    }

    public string GetInstallerDownloadPath()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "YmmpxLibPlugin");
        var fileName = "YmmpxLibPlugin.ymme";
        return Path.Combine(baseDirectory, fileName);
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
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

public sealed class YmmpxLibInstallResult
{
    public bool Success { get; init; }

    public string? DownloadPath { get; init; }

    public string? ErrorMessage { get; init; }
}
