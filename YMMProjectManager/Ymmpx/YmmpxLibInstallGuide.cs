using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using YukkuriMovieMaker.Commons;

namespace YMMProjectManager.Infrastructure.Packaging;

public sealed class YmmpxLibInstallGuide
{
    internal const string SupportedRelease = "v1.1.0";
    internal const string SuggestedFileName = "YmmpxLibPlugin-v1.1.0.ymme";
    public const string YmmpxLibPluginLatestDownloadUrl =
        "https://github.com/hazimeyou/YmmpxLib/releases/download/v1.1.0/YmmpxLibPlugin.ymme";

    private const int OfficialLength = 31_051;
    private const string OfficialSha256 = "9D5EE82C96D6A5ED57D805306CFE037A6669F0D3D15D6C28919E1A59158680DA";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly FileLogger logger;
    private readonly Func<string, CancellationToken, Task<byte[]>> download;
    private readonly int expectedLength;
    private readonly byte[] expectedHash;

    public YmmpxLibInstallGuide(FileLogger logger)
        : this(logger, DownloadFileAsync, OfficialLength, OfficialSha256)
    {
    }

    internal YmmpxLibInstallGuide(FileLogger logger, Func<string, CancellationToken, Task<byte[]>> download,
        int expectedLength, string expectedSha256)
    {
        this.logger = logger;
        this.download = download;
        this.expectedLength = expectedLength;
        expectedHash = Convert.FromHexString(expectedSha256);
    }

    public string GetPassiveStatusMessage() =>
        "YMMPX の作成には YmmpxLibPlugin が必要です。既存の YMMPX はそのまま展開できます。";

    public string BuildMissingPluginMessage(IReadOnlyList<string> searchedPaths, IReadOnlyList<string> legacyPaths) =>
        BuildGuide("YmmpxLibPlugin が見つかりません。", searchedPaths, legacyPaths);

    internal string BuildGuide(string status, IReadOnlyList<string> paths, IReadOnlyList<string> legacyPaths)
    {
        var guide = $"{status}\n\n対応する配布版 {SupportedRelease}\n{YmmpxLibPluginLatestDownloadUrl}\n\n" +
            "保存した .ymme を YMM4 のプラグイン導入手順で開いてください。導入・更新後に YMM4 を再起動してください。" +
            "\n既存の YMMPX の展開は引き続き利用できます。";
        if (legacyPaths.Count != 0)
            guide += "\n\n旧フォルダーが見つかりました。更新前に、中身を確認して必要に応じて手動で退避してください。\n" +
                string.Join("\n", legacyPaths);
        if (paths.Count != 0)
            guide += "\n\n確認した場所\n" + string.Join("\n", paths);
        return guide;
    }

    public IReadOnlyList<string> FindLegacyFolders(string? baseDirectory = null)
    {
        var pluginDirectory = baseDirectory is null
            ? AppDirectories.PluginDirectory
            : Path.Combine(baseDirectory, "user", "plugin");
        return new[]
        {
            Path.Combine(pluginDirectory, "YMMProjectManager", "YmmpxLib"),
            Path.Combine(pluginDirectory, "YmmpxLib"),
        }.Where(Directory.Exists).ToArray();
    }

    internal async Task<YmmpxLibInstallResult?> DownloadInstallerAsync(
        string destination, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) return null;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            destination = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetExtension(destination), ".ymme", StringComparison.OrdinalIgnoreCase))
                throw new IOException(".ymme の保存先を指定してください。");
            var directory = Path.GetDirectoryName(destination)!;
            using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
            if (File.Exists(destination) || Directory.Exists(destination))
                return new() { ErrorMessage = "保存先に既存のファイルがあります。新しい名前を選択してください。" };

            var bytes = await download(YmmpxLibPluginLatestDownloadUrl, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length != expectedLength || !SHA256.HashData(bytes).AsSpan().SequenceEqual(expectedHash))
                return new() { ErrorMessage = $"YmmpxLibPlugin {SupportedRelease} のサイズまたは SHA-256 が公式配布物と一致しません。" };

            await using var file = OwnedFilePublication.CreateOwnedOutputFile(
                Path.Combine(directory, $".ymmpxlib-{Guid.NewGuid():N}.download"));
            var published = false;
            try
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                cancellationToken.ThrowIfCancellationRequested();
                OwnedFilePublication.RenameOpenedPath(file.SafeFileHandle, destination);
                published = true;
                return new() { Success = true, DownloadPath = destination };
            }
            finally
            {
                if (!published) OwnedFilePublication.TryDeleteOpenedPath(file.SafeFileHandle);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   HttpRequestException or OperationCanceledException)
        {
            logger.Info($"YmmpxLibPlugin の取得または保存に失敗しました。{ex.GetType().Name} (0x{ex.HResult:X8})");
            return new() { ErrorMessage = "YmmpxLibPlugin を取得または保存できませんでした。接続と保存先を確認してください。" };
        }
    }

    [Obsolete("Use the confirmed installer download flow.")]
    public Task<YmmpxLibInstallResult> DownloadAndLaunchInstallerAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<YmmpxLibInstallResult>(cancellationToken);
        return Task.FromResult(new YmmpxLibInstallResult
        {
            ErrorMessage = "自動起動は終了しました。YMMPX 作成画面の導入案内から、公式配布物のダウンロードを確認してください。",
        });
    }

    public string GetInstallerDownloadPath() =>
        Path.Combine(Path.GetTempPath(), "YMMProjectManager", "YmmpxLibPlugin", "YmmpxLibPlugin.ymme");

    internal static bool IsOfficialAssetEndpoint(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static async Task<byte[]> DownloadFileAsync(string url, CancellationToken token)
    {
        var current = new Uri(url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (!IsOfficialAssetEndpoint(current))
                throw new HttpRequestException("配布サイトの接続先を確認できません。");
            using var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                current = new Uri(current, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[OfficialLength + 1];
            var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, deadline.Token).ConfigureAwait(false);
            return buffer[..length];
        }
        throw new HttpRequestException("配布サイトの転送回数が上限を超えました。");
    }
}

public sealed class YmmpxLibInstallResult
{
    public bool Success { get; init; }
    public string? DownloadPath { get; init; }
    public string? ErrorMessage { get; init; }
}
