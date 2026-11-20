using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using YMMProjectManager.Infrastructure.Git;

internal static class GitRemoteTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "git-remote"); Directory.CreateDirectory(root);
        var global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var system = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "empty-global"));
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        try
        {
            static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["GIT_ALLOW_PROTOCOL"] = "file:https",
                ["GIT_NO_LAZY_FETCH"] = "0",
            };
            GitRepository.SetTransportEnvironment(environment, allowedProtocol: null);
            Check(environment["GIT_ALLOW_PROTOCOL"] == "" && environment["GIT_NO_LAZY_FETCH"] == "1",
                "preview kept an inherited permission for implicit transport");
            GitRepository.SetTransportEnvironment(environment, "https");
            Check(environment["GIT_ALLOW_PROTOCOL"] == "", "explicit operation widened an inherited deny-all policy");
            environment["GIT_ALLOW_PROTOCOL"] = "file";
            GitRepository.SetTransportEnvironment(environment, "https");
            Check(environment["GIT_ALLOW_PROTOCOL"] == "", "explicit operation widened an inherited allowlist");
            environment["GIT_ALLOW_PROTOCOL"] = "file:https";
            GitRepository.SetTransportEnvironment(environment, "https");
            Check(environment["GIT_ALLOW_PROTOCOL"] == "https" && environment["GIT_NO_LAZY_FETCH"] == "1",
                "explicit operation lost its approved protocol or allowed lazy fetching");
            async Task<string> Command(string cwd, params string[] args)
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); var stdout = await output; var stderr = await error;
                Check(process.ExitCode == 0, "fixture git: " + stderr); return stdout.Trim();
            }
            // A number embedded in a destination path must not be mistaken for an HTTP error.
            var source = Path.Combine(root, "source"); var bare = Path.Combine(root, "remote-403.git"); var clone = Path.Combine(root, "clone");
            Directory.CreateDirectory(source); var one = new GitRepository(source); Check((await one.InitializeAsync()).Success, "init");
            Check(!(await one.FetchAsync()).Success, "missing remote accepted");
            await Command(source, "config", "user.name", "Synthetic Tester"); await Command(source, "config", "user.email", "synthetic@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
            Check((await one.StageAsync(["one.txt"])).Success && (await one.CommitAsync("first")).Success, "initial commit");
            await Command(root, "init", "--bare", bare); await Command(source, "remote", "add", "origin", bare);
            var branch = await Command(source, "symbolic-ref", "--short", "HEAD");
            var expectedCommit = await Command(source, "rev-parse", "HEAD");
            await Command(root, "--git-dir", bare, "symbolic-ref", "HEAD", "refs/heads/" + branch);
            var decoy = Path.Combine(root, "decoy.git");
            await Command(root, "init", "--bare", decoy);
            var oldCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
            var oldKey = Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_0");
            var oldValue = Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_0");
            try
            {
                var redirected = new Uri(decoy).AbsoluteUri;
                Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
                Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", $"url.{redirected}.insteadOf");
                Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", bare);
                Check((await Command(source, "remote", "get-url", "origin")) == redirected, "environment rewrite fixture invalid");
                var actual = await one.OriginAsync();
                Check(actual.Success && actual.StandardOutput == bare, "origin followed inherited config rewrite");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", oldCount);
                Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", oldKey);
                Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", oldValue);
            }
            one.BeforeRemoteExecution = _ =>
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "remote", "set-url", "origin", decoy }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!; process.WaitForExit();
                Check(process.ExitCode == 0, "change remote fixture");
                var switchHead = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "symbolic-ref", "HEAD", "refs/heads/concurrent-unborn" }) switchHead.ArgumentList.Add(arg);
                using var headProcess = Process.Start(switchHead)!; headProcess.WaitForExit();
                Check(headProcess.ExitCode == 0, "change HEAD fixture");
            };
            Check((await one.PushAsync()).Success, "initial push");
            one.BeforeRemoteExecution = null;
            Check(await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch) == expectedCommit, "push used changed HEAD");
            await Command(source, "symbolic-ref", "HEAD", "refs/heads/" + branch);
            Check(!Directory.EnumerateFiles(Path.Combine(decoy, "refs", "heads"), "*", SearchOption.AllDirectories).Any(), "push followed changed origin");
            await Command(source, "remote", "set-url", "origin", bare);
            var initialClone = await GitRepository.CloneAsync(bare, clone);
            Check(initialClone.Success, $"clone failed ({initialClone.Failure}, exit {initialClone.ExitCode}): {initialClone.StandardError}");
            var two = new GitRepository(clone);
            await Command(clone, "config", "user.name", "Synthetic Tester"); await Command(clone, "config", "user.email", "synthetic@example.invalid");
            await Command(clone, "tag", branch);
            await File.WriteAllTextAsync(Path.Combine(clone, "two.txt"), "two");
            Check((await two.StageAsync(["two.txt"])).Success && (await two.CommitAsync("second")).Success && (await two.PushAsync()).Success, "clone push");
            await Command(source, "branch", "race-side");
            var sideBefore = await Command(source, "rev-parse", "refs/heads/race-side");
            one.BeforeRemoteExecution = _ =>
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("switch"); start.ArgumentList.Add("race-side");
                using var process = Process.Start(start)!; process.WaitForExit();
                Check(process.ExitCode == 0, "switch branch during pull fixture");
            };
            var racedPull = await one.PullAsync();
            one.BeforeRemoteExecution = null;
            Check(!racedPull.Success && racedPull.Failure == GitFailure.UnsafeOperation && !string.IsNullOrWhiteSpace(racedPull.StandardError), "raced pull did not report failure");
            Check(await Command(source, "symbolic-ref", "--short", "HEAD") == "race-side", "pull race fixture did not switch branches");
            Check(await Command(source, "rev-parse", "refs/heads/race-side") == sideBefore, "raced pull changed unintended branch");
            Check((await one.InspectPullAsync()).State == GitPullState.TrackingUnavailable, "unknown tracking ref treated as pull candidate");
            await Command(source, "switch", branch);
            using (var cancelledPull = new CancellationTokenSource())
            {
                cancelledPull.Cancel();
                try { await one.PullAsync(cancelledPull.Token); throw new Exception("pull cancellation ignored"); }
                catch (OperationCanceledException) { }
                try { await one.InspectPullAsync(cancelledPull.Token); throw new Exception("pull inspection ignored cancellation"); }
                catch (OperationCanceledException) { }
            }
            Check((await one.FetchAsync()).Success, "fetch");
            var originalIndex = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, ".git", "index")));
            var currentLocal = await Command(source, "rev-parse", "HEAD");
            var currentUpstream = await Command(source, "rev-parse", "refs/remotes/origin/" + branch);
            var checkPull = await one.InspectPullAsync();
            Check(checkPull is { State: GitPullState.FastForwardCandidate } && checkPull.BranchRef == "refs/heads/" + branch &&
                checkPull.LocalObjectId == currentLocal && checkPull.TrackingRef == "refs/remotes/origin/" + branch &&
                checkPull.TrackingObjectId == currentUpstream, "incorrect pull assessment");
            Check(await Command(source, "rev-parse", "HEAD") == currentLocal &&
                await Command(source, "rev-parse", "refs/remotes/origin/" + branch) == currentUpstream &&
                SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, ".git", "index"))).SequenceEqual(originalIndex) &&
                !File.Exists(Path.Combine(source, "two.txt")), "pull assessment modified refs, index or worktree");
            one.BeforePullInspectionRecheck = () =>
            {
                File.WriteAllText(Path.Combine(source, ".git", "HEAD"), "ref: refs/heads/race-side\n");
            };
            Check((await one.InspectPullAsync()).State == GitPullState.Changed, "stale inspection did not detect a changed HEAD");
            one.BeforePullInspectionRecheck = null;
            await Command(source, "switch", branch);
            var untracked = Path.Combine(source, "untracked.txt");
            await File.WriteAllTextAsync(untracked, "keep");
            Check((await one.InspectPullAsync()).State == GitPullState.Dirty && File.Exists(untracked), "untracked data was ignored or lost");
            File.Delete(untracked);
            var disabledPull = await one.PullAsync();
            Check(!disabledPull.Success && disabledPull.Failure == GitFailure.UnsafeOperation, "pull must fail closed");
            Program.ReportConditionalSkip("product fast-forward pull disabled pending cross-process branch protection");
            await Command(source, "merge", "--ff-only", "refs/remotes/origin/" + branch);
            Check(await Command(source, "rev-parse", "HEAD") == await Command(clone, "rev-parse", "HEAD"), "heads differ");
            Check((await one.InspectPullAsync()).State == GitPullState.UpToDate, "matching branches classified as different");
            await File.WriteAllTextAsync(Path.Combine(source, "local.txt"), "local");
            Check((await one.StageAsync(["local.txt"])).Success && (await one.CommitAsync("local")).Success, "local commit");
            Check((await one.InspectPullAsync()).State == GitPullState.LocalAhead, "local-ahead branch classified as pull candidate");
            await File.WriteAllTextAsync(Path.Combine(clone, "remote.txt"), "remote");
            Check((await two.StageAsync(["remote.txt"])).Success && (await two.CommitAsync("remote")).Success && (await two.PushAsync()).Success, "remote commit");
            var prior = await Command(source, "rev-parse", "HEAD");
            var remoteBeforeRejectedPush = await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch);
            var rejectedPush = await one.PushAsync(); var rejectedPull = await one.PullAsync();
            Check(!rejectedPush.Success && !rejectedPull.Success, "divergence accepted");
            Check(rejectedPush.Failure == GitFailure.Divergence && rejectedPull.Failure == GitFailure.UnsafeOperation, $"failure classification: push={rejectedPush.Failure}, pull={rejectedPull.Failure}");
            Check((await one.FetchAsync()).Success, "offline divergence fetch");
            Check((await one.InspectPullAsync()).State == GitPullState.Diverged, "diverged branches classified as pull candidate");
            Check(await Command(source, "rev-parse", "HEAD") == prior, "divergence changed local head");
            Check(await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch) == remoteBeforeRejectedPush, "divergence changed remote head");
            await Command(source, "remote", "set-url", "origin", "https://user:secret@example.invalid/repo");
            var unsafeOrigin = await one.OriginAsync();
            Check(!unsafeOrigin.Success && !unsafeOrigin.StandardOutput.Contains("secret") && !unsafeOrigin.StandardError.Contains("secret"), "credential disclosure");
            await Command(source, "remote", "set-url", "origin", "https://alpha.invalid/repo");
            await Command(source, "config", "--local", "url.https://beta.invalid/.insteadOf", "https://alpha.invalid/");
            await Command(source, "config", "--local", "url.https://gamma.invalid/.insteadOf", "https://beta.invalid/");
            var once = await Command(source, "remote", "get-url", "origin");
            var twice = await Command(source, "ls-remote", "--get-url", once);
            Check(once == "https://beta.invalid/repo" && twice == "https://gamma.invalid/repo", "rewrite fixture changed");
            var rejectedRewrite = await one.FetchAsync();
            Check(!rejectedRewrite.Success && rejectedRewrite.Failure == GitFailure.UnsafeOperation, "fetch should refuse a second URL rewrite before remote execution");
            await Command(source, "remote", "set-url", "origin", bare);
            await Command(source, "config", "--local", "--remove-section", "url.https://beta.invalid/");
            await Command(source, "config", "--local", "--remove-section", "url.https://gamma.invalid/");
            var inspection = await one.InspectRemoteAsync(GitRemoteOperation.Fetch);
            Check(inspection.Success && inspection.StandardOutput == bare && inspection.FetchTarget is { } inspectedFetch &&
                inspectedFetch.Endpoint == bare && inspectedFetch.RepositoryRoot == source, "trusted inspection did not identify the fetch target");
            Check((await two.FetchTrustedAsync(inspection.FetchTarget!)).Failure == GitFailure.UnsafeOperation,
                "fetch approval was accepted from a different checkout");

            var identityRoot = Path.Combine(root, "fetch-identity");
            var firstRoot = Path.Combine(root, "fetch-retained");
            var competingRoot = Path.Combine(root, "fetch-other");
            var movedDuringFetch = Path.Combine(root, "fetch-moved");
            await Command(root, "init", identityRoot);
            await Command(root, "init", competingRoot);
            await Command(identityRoot, "remote", "add", "origin", bare);
            await Command(competingRoot, "remote", "add", "origin", bare);
            var identityRepository = new GitRepository(identityRoot);
            var firstApproval = (await identityRepository.InspectRemoteAsync(GitRemoteOperation.Fetch)).FetchTarget
                ?? throw new InvalidOperationException("missing first repository identity");
            Directory.Move(identityRoot, firstRoot);
            Directory.Move(competingRoot, identityRoot);
            Check((await identityRepository.FetchTrustedAsync(firstApproval)).Failure == GitFailure.UnsafeOperation,
                "same path and endpoint were accepted for a replaced checkout");
            Check((await Command(identityRoot, "for-each-ref", "--format=%(refname)", "refs/remotes")).Length == 0,
                "stale approval modified the replacement checkout");
            var replacementApproval = (await identityRepository.InspectRemoteAsync(GitRemoteOperation.Fetch)).FetchTarget
                ?? throw new InvalidOperationException("missing replacement identity");
            identityRepository.BeforeRemoteExecution = _ =>
            {
                try { Directory.Move(identityRoot, movedDuringFetch); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            };
            Check((await identityRepository.FetchTrustedAsync(replacementApproval)).Success && !Directory.Exists(movedDuringFetch),
                "approved fetch failed or allowed the repository directory to move during execution");
            Check(await Command(identityRoot, "rev-parse", "refs/remotes/origin/" + branch) == remoteBeforeRejectedPush,
                "approved fetch failed to write the expected tracking ref");
            identityRepository.BeforeRemoteExecution = null;
            Directory.Move(Path.Combine(identityRoot, ".git"), Path.Combine(identityRoot, ".git-retained"));
            Directory.Move(Path.Combine(firstRoot, ".git"), Path.Combine(identityRoot, ".git"));
            Check((await identityRepository.FetchTrustedAsync(replacementApproval)).Failure == GitFailure.UnsafeOperation,
                "unchanged worktree path concealed a replacement Git directory");
            Check((await Command(identityRoot, "for-each-ref", "--format=%(refname)", "refs/remotes")).Length == 0,
                "stale approval modified the replacement Git directory");
            await Command(source, "remote", "set-url", "--push", "origin", decoy);
            var pushInspection = await one.InspectRemoteAsync(GitRemoteOperation.Push);
            Check(pushInspection.Success && pushInspection.StandardOutput == decoy, "trusted inspection did not distinguish push URL");
            Check((await one.PushTrustedAsync(bare)).Failure == GitFailure.UnsafeOperation, "push accepted a different endpoint");
            Check(!Directory.EnumerateFiles(Path.Combine(decoy, "refs", "heads"), "*", SearchOption.AllDirectories).Any(), "push mismatch updated unapproved remote");
            await Command(source, "config", "--local", "--unset-all", "remote.origin.pushurl");
            var trustedFetch = await one.FetchTrustedAsync(bare);
            Check(trustedFetch.Success && trustedFetch.RemoteDiagnostic is { Operation: "Fetch" }, "trusted fetch did not complete or report diagnostics");
            Check((await two.PushTrustedAsync(bare)).Success, "trusted non-force push");
            var approval = (await two.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget
                ?? throw new InvalidOperationException("push inspection did not return a target");
            var cloneTip = await Command(clone, "rev-parse", "HEAD");
            Check(approval.Endpoint == bare && approval.BranchRef == "refs/heads/" + branch &&
                approval.ObjectId == cloneTip && approval.RepositoryRoot == clone, "push inspection identified the wrong target");
            Check((await one.PushTrustedAsync(approval)).Failure == GitFailure.UnsafeOperation, "push approval reused for another repository");

            var originalPushCheckout = Path.Combine(root, "push-identity");
            var retainedPushCheckout = Path.Combine(root, "push-retained");
            var alternatePushCheckout = Path.Combine(root, "push-other");
            Check((await GitRepository.CloneTrustedAsync(bare, originalPushCheckout)).Success, "first push identity checkout");
            Check((await GitRepository.CloneTrustedAsync(bare, alternatePushCheckout)).Success, "second push identity checkout");
            var identityPushRepository = new GitRepository(originalPushCheckout);
            var oldPushTarget = (await identityPushRepository.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget
                ?? throw new InvalidOperationException("missing approved push identity");
            Directory.Move(originalPushCheckout, retainedPushCheckout);
            Directory.Move(alternatePushCheckout, originalPushCheckout);
            var currentPushTarget = (await identityPushRepository.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget
                ?? throw new InvalidOperationException("missing replacement push identity");
            Check(currentPushTarget.RepositoryRoot == oldPushTarget.RepositoryRoot && currentPushTarget.Endpoint == oldPushTarget.Endpoint &&
                currentPushTarget.BranchRef == oldPushTarget.BranchRef && currentPushTarget.ObjectId == oldPushTarget.ObjectId,
                "replacement checkout differed in visible approval values");
            Check((await identityPushRepository.PushTrustedAsync(oldPushTarget)).Failure == GitFailure.UnsafeOperation,
                "stale push approval reused against a replacement repository");
            Check((await identityPushRepository.PushTrustedAsync(currentPushTarget)).Success,
                "fresh approval for a stable replacement was rejected");

            const string approvedBranch = "approval-fixture";
            var approvedRef = "refs/heads/" + approvedBranch;
            await Command(clone, "switch", "-c", approvedBranch);
            Check((await two.PushTrustedAsync(approval)).Failure == GitFailure.UnsafeOperation, "push silently switched branch after approval");
            approval = (await two.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget!;
            await Command(clone, "commit", "--allow-empty", "-m", "advance approved branch");
            Check((await two.PushTrustedAsync(approval)).Failure == GitFailure.UnsafeOperation, "push silently advanced the approved commit");
            approval = (await two.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget!;

            two.BeforeRemoteExecution = _ =>
            {
                // HEAD moves after the operation's first validation without changing the approved commit.
                File.WriteAllText(Path.Combine(clone, ".git", "HEAD"), $"ref: refs/heads/{branch}\n");
            };
            Check((await two.PushTrustedAsync(approval)).Failure == GitFailure.UnsafeOperation, "push followed a branch change at launch");
            two.BeforeRemoteExecution = null;
            Check((await Command(root, "--git-dir", bare, "for-each-ref", "--format=%(refname)", approvedRef)).Length == 0,
                "stale approval updated a remote branch");
            await Command(clone, "switch", approvedBranch);
            Check((await two.PushTrustedAsync(approval)).Success, "push rejected the exact approved target");
            Check(await Command(root, "--git-dir", bare, "rev-parse", approvedRef) == approval.ObjectId,
                "push did not publish the approved commit");
            Check(await Command(root, "--git-dir", bare, "rev-parse", "refs/heads/" + branch) == remoteBeforeRejectedPush,
                "push changed an unapproved remote branch");
            var trustedClonePath = Path.Combine(root, "trusted-clone");
            Check((await GitRepository.CloneTrustedAsync(bare, trustedClonePath)).Success && Directory.Exists(trustedClonePath), "trusted clone");
            var nestedRoot = Path.Combine(source, "nested"); Directory.CreateDirectory(nestedRoot);
            Check((await new GitRepository(nestedRoot).FetchTrustedAsync(bare)).Failure == GitFailure.UnsafeOperation, "parent repository used by trust operation");
            await Command(source, "config", "--local", $"url.{decoy}.pushInsteadOf", bare);
            Check((await one.PushTrustedAsync(bare)).Failure == GitFailure.UnsafeOperation, "push used configured rewrite despite literal approval");
            await Command(source, "config", "--local", "--remove-section", $"url.{decoy}");
            const string explicitEndpoint = "https://approved.invalid/repository";
            await Command(source, "remote", "set-url", "origin", explicitEndpoint);
            var unsignedInspection = await one.InspectRemoteAsync(GitRemoteOperation.Fetch);
            Check(unsignedInspection.Success && unsignedInspection.StandardOutput == explicitEndpoint, "HTTPS preview was not literal");
            Check((await one.FetchAsync()).Failure == GitFailure.UnsafeOperation, "HTTPS fetch accepted without explicit trust");
            Check((await one.FetchTrustedAsync("https://other.invalid/repository")).Failure == GitFailure.UnsafeOperation, "trusted fetch accepted wrong HTTPS endpoint");
            var cloneInspection = GitRepository.InspectCloneRemote(explicitEndpoint);
            Check(cloneInspection.Success && cloneInspection.StandardOutput == explicitEndpoint, "clone inspection failed to preserve the approved endpoint");
            Check(!GitRepository.InspectCloneRemote(explicitEndpoint + "=sample").Success,
                "endpoint cannot be represented in scoped Git configuration");
            Check(GitRepository.InspectCloneRemote(explicitEndpoint + "%3Dsample").Success,
                "percent-encoded path data was rejected despite safe configuration representation");
            Check(!GitRepository.InspectCloneRemote(explicitEndpoint + "\u200Bsample").Success,
                "endpoint confirmation accepted invisible format text");
            var credentialInspection = GitRepository.InspectCloneRemote("https://user:secret@approved.invalid/repository");
            Check(!credentialInspection.Success && !credentialInspection.StandardOutput.Contains("secret") &&
                !credentialInspection.StandardError.Contains("secret"), "clone inspection disclosed credentials");
            var unapprovedClone = Path.Combine(root, "unapproved-clone");
            Check((await GitRepository.CloneAsync(explicitEndpoint, unapprovedClone)).Failure == GitFailure.UnsafeOperation &&
                !Directory.Exists(unapprovedClone), "HTTPS clone accepted without explicit trust");
            await Command(source, "remote", "set-url", "origin", bare);
            var timeoutRepository = new GitRepository(source) { RemoteTimeout = TimeSpan.FromSeconds(2) };
            var deadlineObserved = false;
            timeoutRepository.BeforeRemoteExecution = deadline => deadlineObserved = deadline.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            var timedOut = await timeoutRepository.FetchTrustedAsync(bare);
            Check(deadlineObserved && timedOut.Failure == GitFailure.Timeout && timedOut.RemoteDiagnostic is { Operation: "Fetch" }, "remote deadline was not operation-wide");
            try { await GitRepository.CloneAsync("https://user:secret@example.invalid/repo", Path.Combine(root, "unsafe")); throw new Exception("credentials accepted"); } catch (ArgumentException) { }
            try { await GitRepository.CloneAsync(bare, clone); throw new Exception("existing destination accepted"); } catch (IOException) { }
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var cancelledDestination = Path.Combine(root, "cancelled");
                var countBefore = Directory.EnumerateDirectories(root, ".ymmpm-clone-*").Count();
                try { await GitRepository.CloneAsync(bare, cancelledDestination, cancelled.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
                Check(!Directory.Exists(cancelledDestination) && countBefore == Directory.EnumerateDirectories(root, ".ymmpm-clone-*").Count(), "cancelled clone created files");
            }
            var ownedDestination = Path.Combine(root, "owned-clone");
            var existingStages = Directory.EnumerateDirectories(root, ".ymmpm-clone-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? observedStage = null;
            GitDirectoryIdentity? observedIdentity = null;
            var ownedClone = await GitRepository.CloneCoreAsync(bare, ownedDestination, () =>
            {
                observedStage = Directory.EnumerateDirectories(root, ".ymmpm-clone-*").Single(path => !existingStages.Contains(path));
                using var witness = GitRepositoryLease.OpenDirectory(observedStage, FileShare.ReadWrite | FileShare.Delete);
                observedIdentity = GitRepositoryLease.ReadIdentity(witness);
                Check(observedIdentity.HasValue && File.Exists(Path.Combine(observedStage, ".git", "HEAD")),
                    "clone staging did not contain a complete repository");
            }, CancellationToken.None);
            Check(ownedClone.Success && observedIdentity.HasValue && !Directory.Exists(observedStage) &&
                existingStages.All(Directory.Exists), "exact clone failed or affected other staging paths");
            using (var published = GitRepositoryLease.OpenDirectory(ownedDestination))
                Check(GitRepositoryLease.ReadIdentity(published) == observedIdentity,
                    "published directory was not the owned Git staging directory");
            Check(await Command(ownedDestination, "rev-parse", "HEAD") == remoteBeforeRejectedPush,
                "identity-preserving clone differs from the source branch");
            var raced = Path.Combine(root, "raced");
            var race = await GitRepository.CloneCoreAsync(bare, raced, () => { Directory.CreateDirectory(raced); File.WriteAllText(Path.Combine(raced, "sentinel"), "keep"); }, CancellationToken.None);
            Check(!race.Success && race.Failure == GitFailure.UnsafeOperation &&
                race.RecoveryPath is not null && File.Exists(Path.Combine(race.RecoveryPath, ".git", "HEAD")), "race clone recovery");
            Check(File.ReadAllText(Path.Combine(raced, "sentinel")) == "keep", "race overwrote destination");
            using (var publicationCancellation = new CancellationTokenSource())
            {
                var destination = Path.Combine(root, "cancel-at-publication");
                try
                {
                    await GitRepository.CloneCoreAsync(bare, destination, publicationCancellation.Cancel, publicationCancellation.Token);
                    throw new InvalidOperationException("cancelled clone published its destination");
                }
                catch (OperationCanceledException exception)
                {
                    Check(!Directory.Exists(destination) && exception.Data["CloneRecoveryPath"] is string recovery && Directory.Exists(recovery),
                        "cancelled clone did not retain recoverable staging");
                }
            }
            var deep = root;
            while (deep.Length < 225) deep = Path.Combine(deep, "abcdefghij");
            if (deep.Length >= 248) Program.ReportConditionalSkip("long-path clone fixture parent exceeds Windows process working-directory limits");
            else
            {
                Directory.CreateDirectory(deep);
                var deepDestination = Path.Combine(deep, "clone");
                var deepResult = await GitRepository.CloneAsync(bare, deepDestination);
                if (deepResult.Success)
                {
                    Check(Directory.Exists(deepDestination), "long-path clone did not publish its destination");
                    Console.WriteLine("Long-path clone completed.");
                }
                else
                {
                    Check(deepResult.Failure == GitFailure.PathTooLong && !Directory.Exists(deepDestination) &&
                        deepResult.RecoveryPath is not null && Directory.Exists(deepResult.RecoveryPath),
                        $"unsafe or unclassified long-path failure ({deepResult.Failure}, exit {deepResult.ExitCode}): {deepResult.StandardError}");
                    Console.WriteLine("Git filesystem limit reached; owned staging retained and destination unpublished.");
                }
            }
            Console.WriteLine("Offline Git remote integration checks passed.");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global); Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", system); }
    }
}
