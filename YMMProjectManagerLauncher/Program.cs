using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows.Forms;

namespace YMMProjectManagerLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && string.Equals(args[0], "--associate", StringComparison.OrdinalIgnoreCase))
        {
            EnsureFileAssociation();
            MessageBox.Show(".ymmpx の関連付けが完了しました。", "YMM Project Manager Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        try
        {
            var ymmpxPath = ResolveYmmpxPath(args);
            if (ymmpxPath is null)
            {
                return 1;
            }

            var appDir = AppContext.BaseDirectory;
            var extractRoot = GetAvailableDirectoryPath(Path.Combine(appDir, Path.GetFileNameWithoutExtension(ymmpxPath)));

            var restoredYmmp = ExtractBundle(ymmpxPath, extractRoot);
            var ymmExePath = FindYmmExecutable(appDir);
            if (ymmExePath is null)
            {
                MessageBox.Show("YukkuriMovieMaker.exe が見つかりません。", "YMM Project Manager Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = ymmExePath,
                Arguments = $"\"{restoredYmmp}\"",
                UseShellExecute = true,
            });

            if (process is null)
            {
                MessageBox.Show("YMM の起動に失敗しました。", "YMM Project Manager Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            MessageBox.Show(
                "YMM と連携してプロジェクトを開きました。",
                "YMM Project Manager Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"処理に失敗しました。\n{ex.Message}", "YMM Project Manager Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string? ResolveYmmpxPath(string[] args)
    {
        if (args.Length > 0 && File.Exists(args[0]) && Path.GetExtension(args[0]).Equals(".ymmpx", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(args[0]);
        }

        using var dialog = new OpenFileDialog
        {
            Filter = "YMM同梱ファイル (*.ymmpx)|*.ymmpx",
            CheckFileExists = true,
            Multiselect = false,
            Title = "展開する .ymmpx ファイルを選択",
        };

        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return null;
        }

        return Path.GetFullPath(dialog.FileName);
    }

    private static string ExtractBundle(string ymmpxPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var zipStream = new FileStream(ymmpxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var manifestEntry = archive.GetEntry("manifest.json");
        var projectEntry = archive.GetEntry("project.ymmp");
        if (manifestEntry is null || projectEntry is null)
        {
            throw new InvalidOperationException("同梱ファイルの形式が不正です。");
        }

        BundleManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream)
                       ?? new BundleManifest();
        }

        string projectText;
        using (var stream = projectEntry.Open())
        using (var reader = new StreamReader(stream))
        {
            projectText = reader.ReadToEnd();
        }

        var outputRootWithSeparator = EnsureTrailingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in manifest.Files)
        {
            var fileEntry = archive.GetEntry(item.BundlePath);
            if (fileEntry is null)
            {
                continue;
            }

            var safeRelative = item.BundlePath.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(outputDirectory, safeRelative));
            if (!destinationPath.StartsWith(outputRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            using (var source = fileEntry.Open())
            using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
            }

            replacements[item.OriginalPath] = destinationPath;
        }

        foreach (var (oldPath, newPath) in replacements)
        {
            var oldToken = JsonSerializer.Serialize(oldPath);
            var newToken = JsonSerializer.Serialize(newPath);
            var pattern = "(\"FilePath\"\\s*:\\s*)" + Regex.Escape(oldToken);
            projectText = Regex.Replace(projectText, pattern, "$1" + newToken, RegexOptions.CultureInvariant);
        }

        var desiredProjectName = $"{Path.GetFileNameWithoutExtension(ymmpxPath)}.ymmp";
        var ymmpPath = Path.Combine(outputDirectory, desiredProjectName);
        if (File.Exists(ymmpPath))
        {
            var index = 1;
            var baseName = Path.GetFileNameWithoutExtension(desiredProjectName);
            while (File.Exists(ymmpPath))
            {
                ymmpPath = Path.Combine(outputDirectory, $"{baseName}_{index}.ymmp");
                index++;
            }
        }

        File.WriteAllText(ymmpPath, projectText);
        return ymmpPath;
    }

    private static string? FindYmmExecutable(string appDir)
    {
        var current = new DirectoryInfo(appDir);
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var candidate = Path.Combine(current.FullName, "YukkuriMovieMaker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string GetAvailableDirectoryPath(string desiredPath)
    {
        if (!Directory.Exists(desiredPath))
        {
            return desiredPath;
        }

        var suffix = 1;
        while (true)
        {
            var candidate = $"{desiredPath}_{suffix}";
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureFileAssociation()
    {
        const string extension = ".ymmpx";
        const string progId = "YMMProjectManagerYmmpx";
        var appPath = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(AppContext.BaseDirectory, "YMMProjectManagerLauncher.exe");

        using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}"))
        {
            extKey?.SetValue(string.Empty, progId);
        }

        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
        {
            progKey?.SetValue(string.Empty, "YMM Project Bundle File");
            using (var iconKey = progKey?.CreateSubKey("DefaultIcon"))
            {
                iconKey?.SetValue(string.Empty, $"\"{appPath}\",0");
            }

            using (var commandKey = progKey?.CreateSubKey("shell\\open\\command"))
            {
                commandKey?.SetValue(string.Empty, $"\"{appPath}\" \"%1\"");
            }
        }

        NotifyShellAssociationChanged();
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyShellAssociationChanged()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private sealed class BundleManifest
    {
        public List<BundleFileEntry> Files { get; set; } = [];
    }

    private sealed class BundleFileEntry
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string BundlePath { get; set; } = string.Empty;
    }
}
