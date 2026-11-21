using System.ComponentModel;
using System.IO;

namespace YMMProjectManager.Infrastructure.Git;

public sealed partial class GitRepository
{
    internal Func<string, CancellationToken, Task<GitResult?>>? OnStageTreePrepared { get; init; }

    private async Task<GitResult> RunIndexChangeAsync(IReadOnlyCollection<string> requestedPaths, bool stage, CancellationToken token)
    {
        var paths = ReadExplicitIndexPaths(requestedPaths);
        if (HasPendingPullEvidence()) return UnresolvedPull();
        using var session = await OpenPullSessionAsync(token, requireIndex: false).ConfigureAwait(false);
        if (session is null) return UnsafeRemote("Staging requires a verified main repository without unsupported configuration.");
        var gitDirectory = Path.Combine(directory, ".git");
        var indexPath = Path.Combine(gitDirectory, "index");
        try
        {
            if (PullOperationMarkers.Any(marker => HasGitEntry(Path.Combine(gitDirectory, marker)))) return BusyIndex();
            await using var indexLock = OwnedFilePublication.CreateOwnedOutputFile(indexPath + ".lock", readBack: true);
            var published = false;
            try
            {
                if (HasPendingPullEvidence()) return UnresolvedPull();
                if (HasConcurrentCommitMarker(gitDirectory)) return BusyIndex();
                using var head = ProjectListReplacement.Open(Path.Combine(gitDirectory, "HEAD"));
                using var original = ProjectListReplacement.TryOpen(indexPath);
                if (!stage && original is null) return UnsafeRemote("There are no staged files to unstage.");
                using var registry = stage ? OpenAssetRegistryLease() : null;
                if (stage)
                {
                    if (!HasOrdinaryPublicationObjectStore()) return PublicationRefusal("This Git object store cannot be inspected safely.");
                    var known = await ReadKnownPublicationAssetsAsync(token).ConfigureAwait(false);
                    if (known is not null) return known;
                }
                using var scratch = OwnedStagingDirectory.Reserve(gitDirectory, ".ymm-stage-");
                var proposedPath = Path.Combine(scratch.Path, "index");
                (ulong Volume, Guid FileId)? observed = null;
                try
                {
                    if (original is not null)
                    {
                        ProjectListReplacement.VerifyCompatibleFileMetadata(original, indexLock, indexPath);
                        await using var initial = OwnedFilePublication.CreateOwnedOutputFile(proposedPath);
                        observed = ProjectFileEvidence.ReadPhysicalIdentity(initial.SafeFileHandle);
                        await original.CopyToAsync(initial, token).ConfigureAwait(false);
                    }
                    var changed = stage ? await StageExplicitPathsAsync(session, paths, proposedPath, token).ConfigureAwait(false)
                        : await UnstageExplicitPathsAsync(session, paths, proposedPath, token).ConfigureAwait(false);
                    if (changed is not null) return changed;
                    using var candidate = ProjectListReplacement.TryOpen(proposedPath);
                    if (candidate is null) return UnsafeRemote("Git did not prepare a staged index.");
                    observed = ProjectFileEvidence.ReadPhysicalIdentity(candidate.SafeFileHandle);

                    // Only Stage adds selected worktree content. Unstage must remain
                    // available to remove an offending path from pre-existing staging.
                    if (stage)
                    {
                        var write = await RunDirectAsync(["write-tree"], token, pullSettings: session.Settings,
                            privateIndex: proposedPath).ConfigureAwait(false);
                        var tree = write.StandardOutput.Trim();
                        if (!write.Success || !IsObjectId(tree)) return UnsafeRemote("The proposed staged tree could not be verified.");
                        if (OnStageTreePrepared is { } inspect && await inspect(tree, token).ConfigureAwait(false) is { } refusal)
                            return refusal;
                        var mediaError = await CheckPublicationTreeAsync(tree, token).ConfigureAwait(false);
                        if (mediaError is not null) return mediaError;
                    }
                    await candidate.CopyToAsync(indexLock, token).ConfigureAwait(false);
                    await indexLock.FlushAsync(token).ConfigureAwait(false);
                    indexLock.Flush(flushToDisk: true);
                    token.ThrowIfCancellationRequested();
                    if (HasPendingPullEvidence()) return UnresolvedPull();
                    if (HasConcurrentCommitMarker(gitDirectory)) return BusyIndex();
                    // Native Git operations serialize by index.lock. Keep that lock
                    // until the exact checked bytes replace the old index in one rename.
                    original?.Dispose();
                    OwnedFilePublication.RenameOpenedPath(indexLock.SafeFileHandle, indexPath, replaceExisting: original is not null);
                    published = true;
                    return new GitResult(0, "", "");
                }
                finally
                {
                    if (observed is { } identity) TryCleanCommitIndex(proposedPath, identity);
                    TryCleanStageDirectory(scratch);
                }
            }
            finally
            {
                if (!published) OwnedFilePublication.TryDeleteOpenedPath(indexLock.SafeFileHandle);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return BusyIndex();
        }
        catch (InvalidDataException) { return UnsafeRemote("The existing Git index has unsupported file permissions or metadata."); }
        catch (TimeoutException) { return new(1, "", "Staging timed out without publishing the proposed index.", GitFailure.Timeout); }
    }

    private string[] ReadExplicitIndexPaths(IReadOnlyCollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new ArgumentException("At least one explicit path is required.", nameof(paths));
        return paths.Select(path =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (Path.IsPathRooted(path) || HasHiddenText(path))
                throw new ArgumentException("Paths must be relative to the repository.", nameof(paths));
            var full = Path.GetFullPath(path, directory);
            if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Path is outside the repository.", nameof(paths));
            var relative = Path.GetRelativePath(directory, full).Replace('\\', '/');
            if (relative.Split('/').Any(piece => piece.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                piece.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new ArgumentException("Git metadata and alternate data streams are excluded.", nameof(paths));
            return relative;
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<GitResult?> StageExplicitPathsAsync(PullSession session, string[] paths, string index, CancellationToken token)
    {
        var inputs = new List<IDisposable>();
        var present = new List<string>();
        var absent = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var full = Path.Combine(directory, path);
                if (!HasGitEntry(full)) { absent.Add(path); continue; }
                if (!IsRegularFile(full)) return UnsafeRemote("Select individual files. Folders and links cannot be staged.");
                inputs.Add(OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(full)!, enforceShare: true));
                var file = ProjectListReplacement.Open(full);
                inputs.Add(file);
                var mediaError = await CheckStagePublicationFileAsync(path, file, token).ConfigureAwait(false);
                if (mediaError is not null) return mediaError;
                var ignored = await RunDirectAsync(["check-ignore", "--quiet", "--", path], token,
                    pullSettings: session.Settings, privateIndex: index).ConfigureAwait(false);
                if (ignored.ExitCode != 1) return UnsafeRemote("Git ignored the selected file or could not verify its tracking status.");
                present.Add(path);
            }
            if (present.Count > 0 && !await RunIndexInputAsync(session, ["update-index", "--add", "-z", "--stdin"], present, index, token).ConfigureAwait(false))
                return UnsafeRemote("Git could not stage the selected files.");
            if (absent.Count > 0 && !await RunIndexInputAsync(session, ["update-index", "--force-remove", "-z", "--stdin"], absent, index, token).ConfigureAwait(false))
                return UnsafeRemote("Git could not stage the selected removals.");
            return null;
        }
        finally
        {
            for (var i = inputs.Count - 1; i >= 0; i--) inputs[i].Dispose();
        }
    }

    private async Task<GitResult?> UnstageExplicitPathsAsync(PullSession session, string[] paths, string index, CancellationToken token)
    {
        // Explicit files only, even when their worktree paths no longer exist.
        var entries = await RunDirectAsync(["--literal-pathspecs", "ls-files", "-z", "--", .. paths], token,
            pullSettings: session.Settings, outputLimit: 1024 * 1024, privateIndex: index).ConfigureAwait(false);
        var known = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!entries.Success || entries.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(path => !known.Contains(path)))
            return UnsafeRemote("Select individual staged files to unstage.");
        var head = await RunPullCommandAsync(session, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], token).ConfigureAwait(false);
        var parent = head.StandardOutput.Trim();
        if (!head.Success)
            return head.ExitCode == 1 && head.StandardError.Length == 0 &&
                   await RunIndexInputAsync(session, ["update-index", "--force-remove", "-z", "--stdin"], paths, index, token).ConfigureAwait(false)
                ? null : UnsafeRemote("Git could not unstage the selected files from the initial index.");
        if (!IsObjectId(parent)) return UnsafeRemote("The unstage baseline could not be verified.");
        var original = await RunDirectAsync(["ls-tree", "-r", "--full-tree", "--name-only", "-z", parent, "--", .. paths], token,
            pullSettings: session.Settings, outputLimit: 1024 * 1024, privateIndex: index).ConfigureAwait(false);
        if (!original.Success || original.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(path => !known.Contains(path)))
            return UnsafeRemote("Select individual staged files to unstage.");
        var restored = await RunDirectAsync(["--literal-pathspecs", "restore", "--staged", "--source=" + parent, "--", .. paths],
            token, pullSettings: session.Settings, privateIndex: index).ConfigureAwait(false);
        return restored.Success ? null : UnsafeRemote("Git could not unstage the selected files.");
    }

    private async Task<bool> RunIndexInputAsync(PullSession session, string[] args, IReadOnlyCollection<string> paths,
        string index, CancellationToken token) =>
        (await RunDirectAsync(args, token, pullSettings: session.Settings, standardInput: string.Join('\0', paths) + "\0",
            privateIndex: index).ConfigureAwait(false)).Success;

    private static void TryCleanStageDirectory(OwnedStagingDirectory scratch)
    {
        try { scratch.DeleteIfEmpty(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
