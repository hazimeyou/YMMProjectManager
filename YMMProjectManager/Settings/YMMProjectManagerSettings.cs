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

    public void SetSearchFolders(IEnumerable<string> folders)
    {
        SearchFolders = NormalizeFolders(folders);
        SaveToPluginDirectory();
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
        }
        catch
        {
            SearchFolders = [];
        }
    }

    private void SaveToPluginDirectory()
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
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
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
        var assemblyDirectory = Path.GetDirectoryName(typeof(ToolPluginEntry).Assembly.Location) ?? AppContext.BaseDirectory;
        return Path.Combine(assemblyDirectory, SettingsDirectoryName, SettingsFileName);
    }

    private sealed class PersistedSettings
    {
        public List<string>? SearchFolders { get; set; }
    }
}
