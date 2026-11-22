using System.IO;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Plugin;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Settings;

public sealed class YMMProjectManagerSettings : SettingsBase<YMMProjectManagerSettings>
{
    //CSJ,field=SettingsDirectoryName,設定ディレクトリ名
    private const string SettingsDirectoryName = "settings";
    //CSJ,field=SettingsFileName,設定ファイル名前
    private const string SettingsFileName = "YMMProjectManagerSettings.json";

    //CSJ,property=Current,現在の
    public static YMMProjectManagerSettings Current { get; private set; } = new();

    //CSJ,property=Category,分類
    public override SettingsCategory Category => SettingsCategory.None;
    //CSJ,property=Name,名前
    public override string Name => "プロジェクトマネージャー";
    //CSJ,property=HasSettingView,設定ビューの有無
    public override bool HasSettingView => true;
    //CSJ,property=SettingView,設定ビュー
    public override object? SettingView => new YMMProjectManagerSettingsView(this);

    //CSJ,property=SearchFolders,検索フォルダー一覧
    public List<string> SearchFolders { get; private set; } = [];

    //CSJ,method=Initialize,初期化する
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

    //CSJ,method=GetSearchFolders,検索フォルダー一覧を取得する
    public IReadOnlyList<string> GetSearchFolders()
    {
        return SearchFolders.ToArray();
    }

    //CSJ,method=SetSearchFolders,検索フォルダー一覧を設定する
    public bool SetSearchFolders(IEnumerable<string> folders)
    {
        SearchFolders = NormalizeFolders(folders);
        return SaveToPluginDirectory();
    }

    //CSJ,method=Reload,再読み込みする
    public void Reload()
    {
        LoadFromPluginDirectory();
    }

    private void LoadFromPluginDirectory()
    {
        try
        {
            //CSJ,variable=path,パス
            var path = GetSettingsFilePath();
            if (!File.Exists(path))
            {
                SearchFolders = [];
                return;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            //CSJ,variable=data,データ
            var data = JsonSerializer.Deserialize<PersistedSettings>(json);
            SearchFolders = NormalizeFolders(data?.SearchFolders ?? []);
        }
        catch
        {
            SearchFolders = [];
        }
    }

    private bool SaveToPluginDirectory()
    {
        //CSJ,variable=tempPath,一時パス
        string? tempPath = null;
        try
        {
            //CSJ,variable=path,パス
            var path = GetSettingsFilePath();
            //CSJ,variable=directory,ディレクトリ
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            //CSJ,variable=data,データ
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
            return true;
        }
        catch
        {
            return false;
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

    //CSJ,method=NormalizeFolders,フォルダー一覧を正規化する
    private static List<string> NormalizeFolders(IEnumerable<string> folders)
    {
        return folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => NormalizePath(folder.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    //CSJ,method=NormalizePath,パスを正規化する
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

    //CSJ,method=GetSettingsFilePath,設定ファイルパスを取得する
    private static string GetSettingsFilePath()
    {
        //CSJ,variable=assemblyDirectory,アセンブリディレクトリ
        var assemblyDirectory = Path.GetDirectoryName(typeof(YMMProjectManagerSettings).Assembly.Location) ?? AppContext.BaseDirectory;
        return Path.Combine(assemblyDirectory, SettingsDirectoryName, SettingsFileName);
    }

    //CSJ,class=PersistedSettings,永続化済み設定
    private sealed class PersistedSettings
    {
        //CSJ,property=SearchFolders,検索フォルダー一覧
        public List<string>? SearchFolders { get; set; }
    }
}
