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
    private readonly Func<string, Stream, CancellationToken, Task>? downloader;

    public YmmpxLibInstallGuide(FileLogger logger) : this(logger, null, null) { }

    internal YmmpxLibInstallGuide(
        FileLogger logger,
        Action<ProcessStartInfo>? launcher,
        Func<string, Stream, CancellationToken, Task>? downloader)
    {
        this.logger = logger;
        this.launcher = launcher;
        this.downloader = downloader;
    }

    public string GetPassiveStatusMessage()
    {
        return "YmmpxLibPlugin が未導入です。.ymmpx の同梱には導入が必要です。";
    }

    public string BuildMissingPluginMessage(IReadOnlyList<string> searchedPaths, IReadOnlyList<string> legacyPaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("YmmpxLibPlugin が見つかりません。");
        builder.AppendLine();
        builder.AppendLine(".ymmpx の同梱には YmmpxLibPlugin が必要です。");
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
        cancellationToken.ThrowIfCancellationRequested();
        string? downloadPath = null;
        var published = false;
        try
        {
            using var fence = YmmpxPackageReader.ReserveInstallerDirectory(Path.GetTempPath(), out var directory);
            downloadPath = Path.Combine(directory, $"YmmpxLibPlugin-{Guid.NewGuid():N}.ymme");
            logger.Info($"YmmpxLibPlugin のダウンロードを開始します。url={YmmpxLibPluginLatestDownloadUrl}");
            logger.Flush();

            await using (var output = YmmpxPackageReader.CreateOwnedDownload(downloadPath + ".download"))
            {
                try
                {
                    await (downloader ?? DownloadFileAsync)(YmmpxLibPluginLatestDownloadUrl, output, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (output.Length == 0)
                        throw new IOException("インストーラーのファイルが空です。");
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                    cancellationToken.ThrowIfCancellationRequested();
                    YmmpxPackageReader.RenameOpenedPath(output.SafeFileHandle, downloadPath);
                    published = true;
                }
                finally
                {
                    if (!published) YmmpxPackageReader.TryDeleteOpenedPath(output.SafeFileHandle);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"YmmpxLibPlugin の導入に失敗しました。published={published}");
            logger.Flush();
            return new YmmpxLibInstallResult
            {
                Success = false,
                DownloadPath = published ? downloadPath : null,
                ErrorMessage = published
                    ? $"インストーラーを起動できませんでした。ダウンロード済みのファイルを手動で開けます。\n{downloadPath}"
                    : "YmmpxLibPlugin のダウンロードに失敗しました。",
            };
        }
    }

    public string GetInstallerDownloadPath()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "YMMProjectManager", "YmmpxLibPlugin");
        var fileName = "YmmpxLibPlugin.ymme";
        return Path.Combine(baseDirectory, fileName);
    }

    private static async Task DownloadFileAsync(string url, Stream output, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
