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
        if (!HasDirectRepository() || !OnFixedLocalNtfs()) return null;
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
        string[] unsupportedSections = ["include.", "includeif.", "filter.", "url.", "hook.", "http.", "uploadpack.", "credential.", "core.attributesfile", "core.askpass"];
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

    private bool OnFixedLocalNtfs()
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            if (root is not { Length: 3 } || root[1] != ':') return false;
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && string.Equals("NTFS", drive.DriveFormat, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
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
        return true;
    }

    private Task<GitResult> RunPullCommandAsync(PullSession session, string[] arguments, CancellationToken token, Action? afterStart = null) =>
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
        var incoming = await RunPullCommandAsync(session, ["ls-tree", "-r", "-t", "-z", "--full-tree", approved.TargetObjectId], token).ConfigureAwait(false);
        if (!incoming.Success || incoming.StandardError.Length != 0 || !GitPullWindowsTree.CanApply(incoming.StandardOutput))
            return UnsafeRemote("The approved tree contains paths or file types unsupported by Windows Pull.");

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
}
