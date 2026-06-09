using System.IO;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Plugin;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Settings;

public sealed class YMMProjectManagerSettings : SettingsBase<YMMProjectManagerSettings>
{
    private const string SettingsDirectoryName = "settings";
    private const string SettingsFileName = "YMMProjectManagerSettings.json";

    public static YMMProjectManagerSettings Current { get; private set; } = new();

    public override SettingsCategory Category => SettingsCategory.None;
    public override string Name => "プロジェクトマネージャー";
    public override bool HasSettingView => true;
    public override object? SettingView => new YMMProjectManagerSettingsView(this);

    public List<string> SearchFolders { get; private set; } = [];

    public override void Initialize()
    {
        Current = this;
        LoadFromPluginDirectory();
    }

    public YMMProjectManagerSettings()
    {
        Current = this;
        LoadFromPluginDirectory();
    }

    public IReadOnlyList<string> GetSearchFolders()
    {
        return SearchFolders.ToArray();
    }

    public bool ExperimentalFastThumbnailGenerationEnabled { get; set; }

    public int ExperimentalFastThumbnailSampleCount { get; set; } = 64;

    public int ExperimentalFastThumbnailSeekSettleDelayMilliseconds { get; set; } = 50;

    public int ExperimentalFastThumbnailMaxRetryCount { get; set; } = 3;

    public bool ExperimentalFastThumbnailAllowClipboardFallback { get; set; }

    public bool ExperimentalFastThumbnailAllowScreenCaptureFallback { get; set; }

    public (bool Success, string? ErrorMessage) SetSearchFolders(IEnumerable<string> folders)
    {
        var previousSearchFolders = SearchFolders.ToList();
        SearchFolders = NormalizeFolders(folders);
        var result = SaveToPluginDirectory();
        if (!result.Success)
        {
            SearchFolders = previousSearchFolders;
        }

        return result;
    }

    public void Reload()
    {
        LoadFromPluginDirectory();
    }

    private void LoadFromPluginDirectory()
    {
        try
        {
            var path = GetSettingsFilePath();
            if (!File.Exists(path))
            {
                SearchFolders = [];
                return;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<PersistedSettings>(json);
            SearchFolders = NormalizeFolders(data?.SearchFolders ?? []);
            ExperimentalFastThumbnailGenerationEnabled = data?.ExperimentalFastThumbnailGenerationEnabled ?? false;
            ExperimentalFastThumbnailSampleCount = data?.ExperimentalFastThumbnailSampleCount ?? 64;
            ExperimentalFastThumbnailSeekSettleDelayMilliseconds = data?.ExperimentalFastThumbnailSeekSettleDelayMilliseconds ?? 50;
            ExperimentalFastThumbnailMaxRetryCount = data?.ExperimentalFastThumbnailMaxRetryCount ?? 3;
            ExperimentalFastThumbnailAllowClipboardFallback = data?.ExperimentalFastThumbnailAllowClipboardFallback ?? false;
            ExperimentalFastThumbnailAllowScreenCaptureFallback = data?.ExperimentalFastThumbnailAllowScreenCaptureFallback ?? false;
        }
        catch
        {
            SearchFolders = [];
            ExperimentalFastThumbnailGenerationEnabled = false;
            ExperimentalFastThumbnailSampleCount = 64;
            ExperimentalFastThumbnailSeekSettleDelayMilliseconds = 50;
            ExperimentalFastThumbnailMaxRetryCount = 3;
            ExperimentalFastThumbnailAllowClipboardFallback = false;
            ExperimentalFastThumbnailAllowScreenCaptureFallback = false;
        }
    }

    private (bool Success, string? ErrorMessage) SaveToPluginDirectory()
    {
        string? tempPath = null;
        try
        {
            var path = GetSettingsFilePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var data = new PersistedSettings
            {
                SearchFolders = SearchFolders,
                ExperimentalFastThumbnailGenerationEnabled = ExperimentalFastThumbnailGenerationEnabled,
                ExperimentalFastThumbnailSampleCount = ExperimentalFastThumbnailSampleCount,
                ExperimentalFastThumbnailSeekSettleDelayMilliseconds = ExperimentalFastThumbnailSeekSettleDelayMilliseconds,
                ExperimentalFastThumbnailMaxRetryCount = ExperimentalFastThumbnailMaxRetryCount,
                ExperimentalFastThumbnailAllowClipboardFallback = ExperimentalFastThumbnailAllowClipboardFallback,
                ExperimentalFastThumbnailAllowScreenCaptureFallback = ExperimentalFastThumbnailAllowScreenCaptureFallback,
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"設定ファイルの保存に失敗しました: {ex.Message}");
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static List<string> NormalizeFolders(IEnumerable<string> folders)
    {
        return folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => NormalizePath(folder.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string GetSettingsFilePath()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(YMMProjectManagerSettings).Assembly.Location) ?? AppContext.BaseDirectory;
        return Path.Combine(assemblyDirectory, SettingsDirectoryName, SettingsFileName);
    }

    private sealed class PersistedSettings
    {
        public List<string>? SearchFolders { get; set; }

        public bool ExperimentalFastThumbnailGenerationEnabled { get; set; }

        public int ExperimentalFastThumbnailSampleCount { get; set; } = 64;

        public int ExperimentalFastThumbnailSeekSettleDelayMilliseconds { get; set; } = 50;

        public int ExperimentalFastThumbnailMaxRetryCount { get; set; } = 3;

        public bool ExperimentalFastThumbnailAllowClipboardFallback { get; set; }

        public bool ExperimentalFastThumbnailAllowScreenCaptureFallback { get; set; }
    }
}
