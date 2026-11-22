using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Net;

namespace YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitRepository,Gitリポジトリ操作
public sealed partial class GitRepository
{
    // Test fixtures may provide their own root for a literal loopback endpoint. The
    // production service always uses Git's normal certificate trust.
    internal string? LoopbackPullCertificateAuthorityFile { get; init; }

    //CSJ,class=PullSession,プルセッション
    private sealed class PullSession(OwnedFilePublication.DirectoryFence fence, GitRepositoryLease repository,
        FileStream config, string[] settings) : IDisposable
    {
        //CSJ,property=Identity,識別情報
        public GitRepositoryIdentity Identity => repository.Identity;
        //CSJ,property=Settings,設定
        public string[] Settings { get; } = settings;

        //CSJ,method=Dispose,破棄する
        public void Dispose()
        {
            config.Dispose();
            repository.Dispose();
            fence.Dispose();
        }
    }

    //CSJ,method=OpenPullSessionAsync,プルセッションを開く（非同期）
    private async Task<PullSession?> OpenPullSessionAsync(CancellationToken token, bool requireIndex = true)
    {
        token.ThrowIfCancellationRequested();
        if (!OnFixedLocalNtfs(directory) || !HasDirectRepository()) return null;
        //CSJ,variable=gitDirectory,Git ディレクトリ
        var gitDirectory = Path.Combine(directory, ".git");
        //CSJ,variable=fence,整合性境界
        OwnedFilePublication.DirectoryFence? fence = null;
        //CSJ,variable=repository,リポジトリ
        GitRepositoryLease? repository = null;
        FileStream? config = null;
        try
        {
            fence = OwnedFilePublication.DirectoryFence.Open(gitDirectory, enforceShare: true);
            repository = GitRepositoryLease.TryOpen(directory);
            if (repository is null || !SafePullRepositoryMetadata(gitDirectory, requireIndex)) return null;
            config = ProjectListReplacement.Open(Path.Combine(gitDirectory, "config"));

            //CSJ,variable=configuration,構成
            var configuration = await RunDirectAsync(["config", "--local", "--no-includes", "--name-only", "-z", "--list"],
                token, pullSettings: []).ConfigureAwait(false);
            if (!configuration.Success || configuration.StandardError.Length != 0 ||
                configuration.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(UnsupportedPullSetting))
                return null;

            //CSJ,variable=settings,設定
            string[]? settings = await ReadPullCheckoutSettingsAsync(token).ConfigureAwait(false);
            if (settings is null) return null;

            //CSJ,variable=session,セッション
            var session = new PullSession(fence, repository, config, settings);
            fence = null;
            repository = null;
            config = null;
            return session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
        finally
        {
            config?.Dispose();
            repository?.Dispose();
            fence?.Dispose();
        }
    }

    //CSJ,method=UnsupportedPullSetting,未対応プル設定
    private static bool UnsupportedPullSetting(string name)
    {
        string[] unsupportedSections = ["include.", "includeif.", "filter.", "url.", "hook.", "http.", "uploadpack.", "credential.", "core.attributesfile", "core.askpass", "core.sparsecheckout", "index.sparse"];
        return unsupportedSections.Any(section => name.StartsWith(section, StringComparison.OrdinalIgnoreCase)) ||
            name.Equals("extensions.worktreeconfig", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("extensions.partialclone", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("extensions.refstorage", StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(".promisor", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".uploadpack", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".proxy", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<string[]?> ReadPullCheckoutSettingsAsync(CancellationToken token)
    {
        // Global and system config are discarded in the executor. Carry forward the
        // effective built-in line ending settings, which affect ordinary Windows clones.
        //CSJ,variable=allowed,許可済み
        (string Name, string[] Values)[] allowed =
        [
            ("core.autocrlf", ["true", "false", "input"]),
            ("core.eol", ["lf", "crlf", "native"]),
            ("core.safecrlf", ["true", "false", "warn"]),
        ];
        var checkoutPolicies = await RunDirectAsync(
            ["config", "--name-only", "--get-regexp", @"^(core\.attributesfile|branch\..*\.mergeoptions)$"],
            token).ConfigureAwait(false);
        if (checkoutPolicies.ExitCode != 1 || checkoutPolicies.StandardOutput.Length != 0 || checkoutPolicies.StandardError.Length != 0)
            return null;
        //CSJ,variable=signaturePolicy,シグネチャ方針
        var signaturePolicy = await RunDirectAsync(["config", "--type=bool", "--get", "merge.verifySignatures"], token).ConfigureAwait(false);
        if (!((signaturePolicy.ExitCode == 1 && signaturePolicy.StandardOutput.Length == 0) ||
              (signaturePolicy.Success && signaturePolicy.StandardOutput.Trim() == "false")) ||
            signaturePolicy.StandardError.Length != 0)
            return null;

        //CSJ,variable=settings,設定
        var settings = new List<string>();
        foreach (var (name, values) in allowed)
        {
            var result = await RunDirectAsync(["config", "-z", "--get", name], token).ConfigureAwait(false);
            if (result.ExitCode == 1 && result.StandardOutput.Length == 0 && result.StandardError.Length == 0) continue;
            if (!result.Success || result.StandardError.Length != 0 || !result.StandardOutput.EndsWith('\0')) return null;
            var value = result.StandardOutput[..^1].ToLowerInvariant();
            if (!values.Contains(value, StringComparer.Ordinal)) return null;
            settings.Add("-c");
            settings.Add(name + "=" + value);
        }
        return settings.ToArray();
    }

    internal static bool OnFixedLocalNtfs(string path)
    {
        try
        {
            //CSJ,variable=root,ルート
            var root = Path.GetPathRoot(path);
            if (root is not { Length: 3 } || root[1] != ':') return false;
            //CSJ,variable=drive,ドライブ
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && string.Equals("NTFS", drive.DriveFormat, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool SafePullRepositoryMetadata(string gitDirectory, bool requireIndex)
    {
        if ((requireIndex && !IsRegularFile(Path.Combine(gitDirectory, "index"))) || HasGitEntry(Path.Combine(gitDirectory, "shallow")) ||
            HasGitEntry(Path.Combine(gitDirectory, "objects", "info", "alternates")) ||
            HasGitEntry(Path.Combine(gitDirectory, "info", "grafts")))
            return false;
        //CSJ,variable=folder,フォルダー
        foreach (var folder in new[] { "refs", "logs", "info", "objects" })
        {
            //CSJ,variable=path,パス
            var path = Path.Combine(gitDirectory, folder);
            if (!HasGitEntry(path)) continue;
            if (!IsOrdinaryDirectory(path)) return false;
        }
        // Fetch can write loose objects or packs, and merge can append reflogs.
        // A normal .git root does not prove those nested paths stay in it.
        //CSJ,variable=path,パス
        foreach (var path in Directory.EnumerateDirectories(Path.Combine(gitDirectory, "objects")))
            if (!IsOrdinaryDirectory(path)) return false;
        //CSJ,variable=file,ファイル
        foreach (var file in new[] { "logs/HEAD", "ORIG_HEAD", "packed-refs" })
            if (!CanUsePullMetadataFile(gitDirectory, file)) return false;
        return true;
    }

    private static bool CanUsePullMetadataFile(string gitDirectory, string relativePath)
    {
        var components = relativePath.Split('/');
        var current = gitDirectory;
        for (var i = 0; i < components.Length; i++)
        {
            current = Path.Combine(current, components[i]);
            if (!HasGitEntry(current)) return true;
            if (i == components.Length - 1 ? !IsRegularFile(current) : !IsOrdinaryDirectory(current))
                return false;
        }
        return true;
    }

    //CSJ,method=RunPullCommandAsync,プルコマンドを実行する（非同期）
    private Task<GitResult> RunPullCommandAsync(PullSession session, string[] arguments, CancellationToken token, Action<Process>? afterStart = null) =>
        RunDirectAsync(arguments, token, pullSettings: session.Settings, afterStart: afterStart);

    //CSJ,method=RunPullTransportAsync,プル通信を実行する（非同期）
    private async Task<GitResult> RunPullTransportAsync(PullSession session, string endpoint, string[] arguments, CancellationToken token,
        //CSJ,parameter=authentication,認証
        GitHubAuthentication authentication)
    {
        if (HasGitHubHost(endpoint) || authentication != GitHubAuthentication.Anonymous)
            return await RunGitHubTransportAsync(endpoint, arguments, token, authentication, session.Settings).ConfigureAwait(false);
        if (!await VerifyLiteralTransportEndpointAsync(endpoint, token).ConfigureAwait(false))
            return UnsafeRemote("The Pull endpoint can also resolve as a configured remote name.");
        //CSJ,variable=local,ローカル
        var local = Path.IsPathFullyQualified(endpoint);
        //CSJ,variable=settings,設定
        var settings = local ? Array.Empty<string>() :
            new[] { "-c", "http.followRedirects=false", "-c", "http.sslVerify=true", "-c", "http.proxy=" };
        if (LoopbackPullCertificateAuthorityFile is { } authority &&
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 &&
            IPAddress.TryParse(uri.DnsSafeHost, out var address) &&
            (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback)))
            settings = [.. settings, "-c", $"http.sslCAInfo={Path.GetFullPath(authority)}"];
        return await RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}", .. settings, .. arguments],
            token, allowedProtocol: local ? "file" : "https", trustedProcess: true, pullSettings: session.Settings).ConfigureAwait(false);
    }

    private async Task<GitResult?> VerifyIncomingPullPathsAsync(PullSession session, GitPullTarget approved, CancellationToken token)
    {
        //CSJ,variable=treeOutputLimit,ツリー出力上限
        const int treeOutputLimit = 8 * 1024 * 1024;
        var incoming = await RunDirectAsync(["ls-tree", "-r", "-t", "-z", "--full-tree", approved.TargetObjectId], token,
            pullSettings: session.Settings, outputLimit: treeOutputLimit).ConfigureAwait(false);
        if (!incoming.Success || incoming.StandardError.Length != 0 ||
            !GitPullWindowsTree.TryReadFilePaths(incoming.StandardOutput, out var files))
            return UnsafeRemote("The approved tree contains paths or file types unsupported by Windows Pull.");
        if (!await HasOrdinaryPullAttributesAsync(session, approved.TargetObjectId, files, token).ConfigureAwait(false))
            return UnsafeRemote("The approved tree needs an external checkout filter or its attributes could not be verified.");

        //CSJ,variable=changed,変更済み
        var changed = await RunPullCommandAsync(session,
            ["diff-tree", "-z", "--name-only", "--no-commit-id", "--no-renames", "--no-ext-diff", "--no-textconv", "-r",
                approved.StartObjectId, approved.TargetObjectId, "--"], token).ConfigureAwait(false);
        if (!changed.Success || changed.StandardError.Length != 0) return UnsafeRemote("Could not verify affected worktree paths.");
        //CSJ,variable=path,パス
        foreach (var path in changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=local,ローカル
            var local = directory;
            //CSJ,variable=component,構成要素
            foreach (var component in path.Split('/'))
            {
                if (component is "" or "." or ".." || component.Contains('\\') || component.Contains(':') || HasHiddenText(component))
                    return UnsafeRemote("A changed worktree path is unsupported.");
                local = Path.Combine(local, component);
                //CSJ,variable=attributes,属性
                FileAttributes attributes;
                try { attributes = File.GetAttributes(local); }
                catch (FileNotFoundException) { break; }
                catch (DirectoryNotFoundException) { break; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return UnsafeRemote("A changed worktree path traverses a reparse point.");
            }
        }
        return null;
    }

    //CSJ,method=HasOrdinaryPullAttributesAsync,通常プル属性の有無を判定する（非同期）
    private async Task<bool> HasOrdinaryPullAttributesAsync(PullSession session, string commit, List<string> files,
        CancellationToken token)
    {
        if (files.Count == 0) return true;
        //CSJ,variable=attributeOutputLimit,属性出力上限
        const int attributeOutputLimit = 8 * 1024 * 1024;
        GitResult result;
        try
        {
            // --all omits truly unspecified attributes. A literal driver name like "unset"
            // otherwise has the same displayed value as a disabled attribute.
            result = await RunDirectAsync(["check-attr", "--source=" + commit, "--stdin", "-z", "--all"],
                token, pullSettings: session.Settings, standardInput: string.Join('\0', files) + '\0',
                outputLimit: attributeOutputLimit).ConfigureAwait(false);
        }
        catch (IOException) { return false; }
        if (!result.Success || result.StandardError.Length != 0) return false;
        //CSJ,variable=fields,フィールド一覧
        var fields = result.StandardOutput.Split('\0');
        if (fields.Length % 3 != 1 || fields[^1].Length != 0) return false;
        //CSJ,variable=paths,パス一覧
        var paths = files.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length - 1; i += 3)
            if (!paths.Contains(fields[i]) || fields[i + 1].Length == 0 || fields[i + 1] == "filter")
                return false;
        return true;
    }
}
