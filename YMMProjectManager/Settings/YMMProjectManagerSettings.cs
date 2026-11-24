using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Infrastructure;
using YukkuriMovieMaker.Plugin;

namespace YMMProjectManager.Settings;

//CSJ,class=YMMProjectManagerSettings,プロジェクト管理設定
public sealed class YMMProjectManagerSettings : SettingsBase<YMMProjectManagerSettings>
{
    //CSJ,field=SettingsDirectoryName,設定ディレクトリ名
    private const string SettingsDirectoryName = "settings";
    //CSJ,field=SettingsFileName,設定ファイル名前
    private const string SettingsFileName = "YMMProjectManagerSettings.json";

    private static readonly object SaveLock = new();
    private readonly string settingsPath;
    private readonly Action? beforePublish;
    private bool hasConfirmedSettings;
    private SettingsVersion? confirmedVersion;

    private sealed record SettingsVersion((ulong, Guid) Identity, string Sha256);

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
        Reload();
        Current = this;
    }

    public YMMProjectManagerSettings() : this(GetSettingsFilePath())
    {
        Current = this;
    }

    internal YMMProjectManagerSettings(string settingsPath, Action? beforePublish = null)
    {
        this.settingsPath = Path.GetFullPath(settingsPath);
        this.beforePublish = beforePublish;
        Reload();
    }

    //CSJ,method=GetSearchFolders,検索フォルダー一覧を取得する
    public IReadOnlyList<string> GetSearchFolders()
    {
        lock (SaveLock) return SearchFolders.ToArray();
    }

    //CSJ,method=SetSearchFolders,検索フォルダー一覧を設定する
    public bool SetSearchFolders(IEnumerable<string> folders)
    {
        lock (SaveLock)
        {
            try
            {
                return hasConfirmedSettings && TrySave(NormalizeFolders(folders));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return false;
            }
        }
    }

    //CSJ,method=Reload,再読み込みする
    public void Reload() => TryReload();

    internal bool TryReload()
    {
        lock (SaveLock)
        {
            try
            {
                using var input = OpenCurrent(allowRename: false);
                var bytes = input is null ? null : ReadSettings(input);
                var data = bytes is null ? null : DecodeSettings(bytes)
                    ?? throw new InvalidDataException("Invalid settings.");
                var folders = NormalizeFolders(data?.SearchFolders ?? []);
                confirmedVersion = input is null ? null : GetVersion(input, bytes!);
                SearchFolders = folders;
                hasConfirmedSettings = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // Preserve the last confirmed values and prevent saves until a reload succeeds.
                hasConfirmedSettings = false;
                return false;
            }
        }
    }

    private bool TrySave(List<string> folders)
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(directory);
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        using var original = OpenCurrent(allowRename: true);
        var currentVersion = original is null ? null : GetVersion(original, ReadSettings(original));
        if (currentVersion != confirmedVersion) return false;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PersistedSettings { SearchFolders = folders },
            new JsonSerializerOptions { WriteIndented = true });
        var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var output = OwnedFilePublication.CreateOwnedOutputFile(temporary);
        var parkedPath = settingsPath + ".previous-" + Guid.NewGuid().ToString("N");
        var parked = false;
        var published = false;
        try
        {
            var stagedVersion = GetVersion(output, bytes);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
            if (original is not null)
            {
                OwnedFilePublication.RenameOpenedPath(original.SafeFileHandle, parkedPath);
                parked = true;
            }

            beforePublish?.Invoke();
            OwnedFilePublication.RenameOpenedPath(output.SafeFileHandle, settingsPath);
            published = true;
            confirmedVersion = stagedVersion;
            SearchFolders = folders;
            if (parked) OwnedFilePublication.TryDeleteOpenedPath(original!.SafeFileHandle);
            return true;
        }
        finally
        {
            if (!published)
            {
                if (parked)
                {
                    try { OwnedFilePublication.RenameOpenedPath(original!.SafeFileHandle, settingsPath); }
                    catch (IOException)
                    {
                        // A competing file occupies the active path. Keep both files.
                        PluginDiagnostics.Logger.Info($"Settings rollback preserved {Path.GetFileName(parkedPath)}.");
                    }
                }
                OwnedFilePublication.TryDeleteOpenedPath(output.SafeFileHandle);
            }
        }
    }

    private FileStream? OpenCurrent(bool allowRename)
    {
        try
        {
            File.GetAttributes(settingsPath);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            var directory = Path.GetDirectoryName(settingsPath)!;
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory,
                Path.GetFileName(settingsPath) + ".previous-*", SearchOption.TopDirectoryOnly).Any())
                throw new IOException("The current settings name is missing while previous data is retained.");
            return null;
        }

        var handle = OwnedFilePublication.OpenOrdinaryPath(settingsPath, expectDirectory: false, allowRename);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    private static byte[] ReadSettings(FileStream input)
    {
        if (input.Length > Array.MaxLength) throw new InvalidDataException("The settings file exceeds the read limit.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static SettingsVersion GetVersion(FileStream input, byte[] bytes) =>
        new(ProjectFileEvidence.ReadPhysicalIdentity(input), Convert.ToHexString(SHA256.HashData(bytes)));

    private static PersistedSettings? DecodeSettings(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false),
            Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonSerializer.Deserialize<PersistedSettings>(reader.ReadToEnd());
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
