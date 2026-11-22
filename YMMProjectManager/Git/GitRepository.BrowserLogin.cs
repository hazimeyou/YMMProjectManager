using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitRepository,Gitリポジトリ操作
public sealed partial class GitRepository
{
    // Exact Windows x64 single-file payload of the signed official GCM 3.0.2 release.
    //CSJ,field=BrowserGcmSha256,ブラウザーGCMSHA-256
    internal const string BrowserGcmSha256 = "A139DBB2DB5238266B53D6596FA02880A25CDB27D709DA547147C28159D40CF0";
    //CSJ,field=BrowserLoginGateName,ブラウザーログイン排他制御名前
    private const string BrowserLoginGateName = @"Local\YMMProjectManager.GitHubBrowserLogin";
    private static readonly TimeSpan BrowserLoginDeadline = TimeSpan.FromMinutes(3);

    //CSJ,method=GetGitHubBrowserLoginAvailabilityAsync,ブラウザーでのGitHubログイン可否を取得する（非同期）
    public static Task<bool> GetGitHubBrowserLoginAvailabilityAsync(CancellationToken token = default) =>
        Task.Run(async () =>
        {
            //CSJ,variable=installation,インストール
            using var installation = await OpenBrowserGcmAsync(token).ConfigureAwait(false);
            return installation is not null;
        }, token);

    //CSJ,method=SignInGitHubInBrowserAsync,ブラウザーでGitHubにログインする（非同期）
    public static Task<GitResult> SignInGitHubInBrowserAsync(bool reauthenticate, CancellationToken token = default) =>
        Task.Run(() => RunGitHubBrowserLoginAsync(reauthenticate, token), token);

    //CSJ,method=RunGitHubBrowserLoginAsync,GitHubブラウザーログインを実行する（非同期）
    private static async Task<GitResult> RunGitHubBrowserLoginAsync(bool reauthenticate, CancellationToken token)
    {
        //CSJ,variable=installation,インストール
        using var installation = await OpenBrowserGcmAsync(token).ConfigureAwait(false);
        if (installation is null)
            return UnsafeRemote("Browser login requires the verified machine installation of Git Credential Manager 3.0.2 (Windows x64).");

        try
        {
            //CSJ,variable=gate,排他制御
            using var gate = new Semaphore(1, 1, BrowserLoginGateName);
            if (!gate.WaitOne(0))
                return new GitResult(1, "", "Another browser login is already running.", GitFailure.ConcurrentOperation);
            try
            {
                token.ThrowIfCancellationRequested();
                await using var proxy = new GitHubBrowserProxy();
                //CSJ,variable=environment,環境
                var environment = BuildBrowserGcmEnvironment(installation.Directory, installation.GitDirectory, proxy.Endpoint);
                token.ThrowIfCancellationRequested();
                //CSJ,variable=helper,補助処理
                using var helper = GitHubLoginProcess.Start(installation.Executable, BrowserGcmArguments(reauthenticate),
                    installation.Directory, environment);
                return await FinishBrowserLoginAsync(helper, proxy, BrowserLoginDeadline, token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or SocketException or IOException or
            WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            return UnsafeRemote("The browser helper could not start in the verified Windows execution environment.");
        }
    }

    internal static async Task<GitResult> FinishBrowserLoginAsync(GitHubLoginProcess helper, GitHubBrowserProxy proxy,
        //CSJ,parameter=timeout,タイムアウト
        TimeSpan timeout, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(timeout);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        //CSJ,variable=register,登録
        using var register = stop.Token.Register(() => completed.TrySetResult());
        await Task.WhenAny(helper.Exited, completed.Task).ConfigureAwait(false);

        if (!helper.HasExited)
        {
            //CSJ,variable=cancelled,キャンセル済み
            var cancelled = token.IsCancellationRequested;
            // Keep the proxy's exclusive listener bound until the helper's actual exit,
            // even when Windows cannot confirm termination immediately.
            proxy.StopForwarding();
            helper.Terminate();
            await helper.Exited.ConfigureAwait(false);
            return cancelled ? UnknownBrowserOutcome() : new GitResult(1, "",
                "Browser login timed out. Its credential-store outcome could not be confirmed.", GitFailure.Timeout);
        }

        try
        {
            if (helper.ReadExitCode() != 0)
                return new GitResult(1, "", "The credential manager did not complete browser login.", GitFailure.Authentication);
        }
        catch (Win32Exception) { return UnknownBrowserOutcome(); }

        // A successful browser exchange must have traversed the verified proxy. The
        // helper owns WinCred, so unverifiable publication must be treated as uncertain.
        return proxy.Opened && !proxy.Denied ? new GitResult(0, "", "") : UnknownBrowserOutcome();
    }

    //CSJ,method=UnknownBrowserOutcome,不明ブラウザー処理結果
    private static GitResult UnknownBrowserOutcome() =>
        new(1, "", "Browser login or its credential-store outcome could not be confirmed.", GitFailure.OutcomeUnknown);

    //CSJ,method=BrowserGcmArguments,ブラウザーGCM引数一覧
    internal static string BrowserGcmArguments(bool reauthenticate) =>
        "github login --url https://github.com --browser" + (reauthenticate ? " --force" : "");

    //CSJ,method=BuildBrowserGcmEnvironment,ブラウザーGCM環境を構築する
    internal static Dictionary<string, string> BuildBrowserGcmEnvironment(string workingDirectory, string gitDirectory, Uri proxy)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        //CSJ,variable=localAppData,ローカルアプリケーションデータ
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Path.GetDirectoryName(Environment.SystemDirectory)!;
        // This intentionally absent random path lives in the independently verified,
        // machine-protected working directory. Git treats a missing global config as empty.
        var emptyGlobalConfig = Path.Combine(workingDirectory, $".ymmpm-no-config-{Guid.NewGuid():N}");
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["PATH"] = string.Join(Path.PathSeparator, gitDirectory, Environment.SystemDirectory),
            ["PATHEXT"] = ".COM;.EXE",
            ["USERPROFILE"] = userProfile,
            ["LOCALAPPDATA"] = localAppData,
            ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["TEMP"] = Path.Combine(localAppData, "Temp"),
            ["TMP"] = Path.Combine(localAppData, "Temp"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_SYSTEM"] = emptyGlobalConfig,
            ["GIT_CONFIG_GLOBAL"] = emptyGlobalConfig,
            ["GIT_EXEC_PATH"] = gitDirectory,
            ["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(workingDirectory)!,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "1",
            ["GCM_GUI_PROMPT"] = "0",
            ["GCM_PROVIDER"] = "github",
            ["GCM_CREDENTIAL_STORE"] = "wincredman",
        };

        // Explicit synthetic proxy credentials prevent HTTP clients offering Windows
        // default credentials if a proxy endpoint ever asks. The private relay does
        // not request or use these credentials.
        var proxyUri = new UriBuilder(proxy) { UserName = "local", Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) };
        //CSJ,variable=settings,設定
        (string Name, string Value)[] settings =
        [
            ("http.proxy", proxyUri.Uri.AbsoluteUri),
            ("http.sslVerify", "true"),
            ("credential.helper", ""),
        ];
        env["GIT_CONFIG_COUNT"] = settings.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < settings.Length; i++)
        {
            env[$"GIT_CONFIG_KEY_{i}"] = settings[i].Name;
            env[$"GIT_CONFIG_VALUE_{i}"] = settings[i].Value;
        }
        return env;
    }

    //CSJ,method=OpenBrowserGcmAsync,ブラウザーGCMを開く（非同期）
    private static async Task<BrowserGcmInstallation?> OpenBrowserGcmAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64) return null;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!OnFixedLocalNtfs(programFiles)) return null;
        //CSJ,variable=gitDirectory,Git ディレクトリ
        var gitDirectory = Path.Combine(MachineGitInstallation(), "mingw64", "bin");
        //CSJ,variable=path,パス
        foreach (var path in new[] { Path.Combine(programFiles, "Git Credential Manager"), gitDirectory })
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=helperFence,補助処理整合性境界
            OwnedFilePublication.DirectoryFence? helperFence = null;
            //CSJ,variable=gitFence,Git整合性境界
            OwnedFilePublication.DirectoryFence? gitFence = null;
            //CSJ,variable=helper,補助処理
            FileStream? helper = null;
            FileStream? git = null;
            try
            {
                helperFence = OwnedFilePublication.DirectoryFence.Open(path, enforceShare: true);
                gitFence = OwnedFilePublication.DirectoryFence.Open(gitDirectory, enforceShare: true);
                if (!ProtectedInstallationPath(path, programFiles) || !ProtectedInstallationPath(gitDirectory, programFiles) ||
                    Path.Exists(Path.Combine(path, ".git")))
                    continue;

                //CSJ,variable=executable,実行ファイル
                var executable = Path.Combine(path, "git-credential-manager.exe");
                helper = ProjectListReplacement.Open(executable);
                if (!IsProtectedInstallationEntry(helper.GetAccessControl()) || helper.Length != 31_178_088 ||
                    Convert.ToHexString(await SHA256.HashDataAsync(helper, token).ConfigureAwait(false)) != BrowserGcmSha256)
                    continue;
                git = ProjectListReplacement.Open(Path.Combine(gitDirectory, "git.exe"));
                if (!IsProtectedInstallationEntry(git.GetAccessControl())) continue;
                //CSJ,variable=installation,インストール
                var installation = new BrowserGcmInstallation(executable, path, gitDirectory, helper, git, helperFence, gitFence);
                (helper, git, helperFence, gitFence) = (null, null, null, null);
                return installation;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { }
            finally
            {
                helper?.Dispose();
                git?.Dispose();
                helperFence?.Dispose();
                gitFence?.Dispose();
            }
        }

        return null;
    }

    private static bool ProtectedInstallationPath(string path, string programFiles)
    {
        // Bundled managed code is part of the hash. Also refuse machine tool
        // directories with writable ACLs for non-administrative principals.
        for (var cursor = new DirectoryInfo(path); cursor is not null; cursor = cursor.Parent)
        {
            var acl = cursor.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (!IsProtectedInstallationEntry(acl)) return false;
            if (string.Equals(cursor.FullName, programFiles, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static bool IsProtectedInstallationEntry(FileSystemSecurity acl)
    {
        const FileSystemRights writeRights = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership | (FileSystemRights)0x50000000;
        return new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is not null &&
            IsInstallationAdministrator(acl.GetOwner(typeof(SecurityIdentifier))) &&
            !acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                    (rule.FileSystemRights & writeRights) != 0 && !IsInstallationAdministrator(rule.IdentityReference));
    }

    private static bool IsInstallationAdministrator(IdentityReference? identity) =>
        identity?.Value is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    //CSJ,class=BrowserGcmInstallation,ブラウザーGCMインストール
    private sealed class BrowserGcmInstallation(string executable, string directory, string gitDirectory,
        FileStream helper, FileStream git, OwnedFilePublication.DirectoryFence helperFence,
        OwnedFilePublication.DirectoryFence gitFence) : IDisposable
    {
        //CSJ,property=Executable,実行ファイル
        public string Executable => executable;
        //CSJ,property=Directory,ディレクトリ
        public string Directory => directory;
        //CSJ,property=GitDirectory,Git ディレクトリ
        public string GitDirectory => gitDirectory;

        //CSJ,method=Dispose,破棄する
        public void Dispose()
        {
            helper.Dispose();
            git.Dispose();
            helperFence.Dispose();
            gitFence.Dispose();
        }
    }
}
