using System.Diagnostics;
using System.ComponentModel;
using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

public sealed partial class GitRepository
{
    private sealed class PullSession(OwnedFilePublication.DirectoryFence fence, GitRepositoryLease repository,
        FileStream config, string[] settings) : IDisposable
    {
        public GitRepositoryIdentity Identity => repository.Identity;
        public string[] Settings { get; } = settings;

        public void Dispose()
        {
            config.Dispose();
            repository.Dispose();
            fence.Dispose();
        }
    }

    private async Task<PullSession?> OpenPullSessionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OnFixedLocalNtfs(directory) || !HasDirectRepository()) return null;
        var gitDirectory = Path.Combine(directory, ".git");
        OwnedFilePublication.DirectoryFence? fence = null;
        GitRepositoryLease? repository = null;
        FileStream? config = null;
        try
        {
            fence = OwnedFilePublication.DirectoryFence.Open(gitDirectory, enforceShare: true);
            repository = GitRepositoryLease.TryOpen(directory);
            if (repository is null || !SafePullRepositoryMetadata(gitDirectory)) return null;
            config = ProjectListReplacement.Open(Path.Combine(gitDirectory, "config"));

            var configuration = await RunDirectAsync(["config", "--local", "--no-includes", "--name-only", "-z", "--list"],
                token, pullSettings: []).ConfigureAwait(false);
            if (!configuration.Success || configuration.StandardError.Length != 0 ||
                configuration.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(UnsupportedPullSetting))
                return null;

            string[]? settings = await ReadPullCheckoutSettingsAsync(token).ConfigureAwait(false);
            if (settings is null) return null;

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
        var signaturePolicy = await RunDirectAsync(["config", "--type=bool", "--get", "merge.verifySignatures"], token).ConfigureAwait(false);
        if (!((signaturePolicy.ExitCode == 1 && signaturePolicy.StandardOutput.Length == 0) ||
              (signaturePolicy.Success && signaturePolicy.StandardOutput.Trim() == "false")) ||
            signaturePolicy.StandardError.Length != 0)
            return null;

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
            var root = Path.GetPathRoot(path);
            if (root is not { Length: 3 } || root[1] != ':') return false;
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && string.Equals("NTFS", drive.DriveFormat, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool SafePullRepositoryMetadata(string gitDirectory)
    {
        if (!IsRegularFile(Path.Combine(gitDirectory, "index")) || HasGitEntry(Path.Combine(gitDirectory, "shallow")) ||
            HasGitEntry(Path.Combine(gitDirectory, "objects", "info", "alternates")) ||
            HasGitEntry(Path.Combine(gitDirectory, "info", "grafts")))
            return false;
        foreach (var folder in new[] { "refs", "logs", "info", "objects" })
        {
            var path = Path.Combine(gitDirectory, folder);
            if (!HasGitEntry(path)) continue;
            if (!IsOrdinaryDirectory(path)) return false;
        }
        // Fetch can write loose objects or packs, and merge can append reflogs.
        // A normal .git root does not prove those nested paths stay in it.
        foreach (var path in Directory.EnumerateDirectories(Path.Combine(gitDirectory, "objects")))
            if (!IsOrdinaryDirectory(path)) return false;
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

    private Task<GitResult> RunPullCommandAsync(PullSession session, string[] arguments, CancellationToken token, Action<Process>? afterStart = null) =>
        RunDirectAsync(arguments, token, pullSettings: session.Settings, afterStart: afterStart);

    private Task<GitResult> RunPullTransportAsync(PullSession session, string endpoint, string[] arguments, CancellationToken token)
    {
        var local = Path.IsPathFullyQualified(endpoint);
        var settings = local ? Array.Empty<string>() :
            new[] { "-c", "http.followRedirects=false", "-c", "http.sslVerify=true", "-c", "http.proxy=" };
        return RunAsync([$"--git-dir={Path.Combine(directory, ".git")}", $"--work-tree={directory}", .. settings, .. arguments],
            token, allowedProtocol: local ? "file" : "https", trustedProcess: true, pullSettings: session.Settings);
    }

    private async Task<GitResult?> VerifyIncomingPullPathsAsync(PullSession session, GitPullTarget approved, CancellationToken token)
    {
        const int treeOutputLimit = 8 * 1024 * 1024;
        var incoming = await RunDirectAsync(["ls-tree", "-r", "-t", "-z", "--full-tree", approved.TargetObjectId], token,
            pullSettings: session.Settings, outputLimit: treeOutputLimit).ConfigureAwait(false);
        if (!incoming.Success || incoming.StandardError.Length != 0 ||
            !GitPullWindowsTree.TryReadFilePaths(incoming.StandardOutput, out var files))
            return UnsafeRemote("The approved tree contains paths or file types unsupported by Windows Pull.");
        if (!await HasOrdinaryPullAttributesAsync(session, approved.TargetObjectId, files, token).ConfigureAwait(false))
            return UnsafeRemote("The approved tree needs an external checkout filter or its attributes could not be verified.");

        var changed = await RunPullCommandAsync(session,
            ["diff-tree", "-z", "--name-only", "--no-commit-id", "--no-renames", "--no-ext-diff", "--no-textconv", "-r",
                approved.StartObjectId, approved.TargetObjectId, "--"], token).ConfigureAwait(false);
        if (!changed.Success || changed.StandardError.Length != 0) return UnsafeRemote("Could not verify affected worktree paths.");
        foreach (var path in changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            var local = directory;
            foreach (var component in path.Split('/'))
            {
                if (component is "" or "." or ".." || component.Contains('\\') || component.Contains(':') || HasHiddenText(component))
                    return UnsafeRemote("A changed worktree path is unsupported.");
                local = Path.Combine(local, component);
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

    private async Task<bool> HasOrdinaryPullAttributesAsync(PullSession session, string commit, List<string> files,
        CancellationToken token)
    {
        if (files.Count == 0) return true;
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
        var fields = result.StandardOutput.Split('\0');
        if (fields.Length % 3 != 1 || fields[^1].Length != 0) return false;
        var paths = files.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length - 1; i += 3)
            if (!paths.Contains(fields[i]) || fields[i + 1].Length == 0 || fields[i + 1] == "filter")
                return false;
        return true;
    }
}
