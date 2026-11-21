using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using YMMProjectManager.Infrastructure.Git;

internal static class GitPublicationTests
{
    internal static async Task RunAsync(string root)
    {
        await Program.RunNamedCaseAsync("R21 Push accepts checked plain history and ignores pre-push hook", () =>
            TestAsync(root, "plain", async (source, _) =>
            {
                var marker = Path.Combine(source, "local-hook-ran");
                await File.WriteAllTextAsync(Path.Combine(source, ".git", "hooks", "pre-push"),
                    "#!/bin/sh\ntouch local-hook-ran\nexit 1\n");
                await CommitFileAsync(source, "README.txt", Encoding.UTF8.GetBytes("Japanese 日本語\n"));
                return (true, marker);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses bytes in deleted or renamed history", () =>
            TestAsync(root, "binary", async (source, _) =>
            {
                await CommitFileAsync(source, "unmarked.txt", [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 5]);
                await CommandAsync(source, "mv", "unmarked.txt", "renamed.txt");
                await CommandAsync(source, "commit", "-m", "rename");
                await CommandAsync(source, "rm", "renamed.txt");
                await CommandAsync(source, "commit", "-m", "remove");
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses PF-referenced plain text media", () =>
            TestAsync(root, "pf", async (source, _) =>
            {
                await CommitFileAsync(source, "scene.ymmp", """{"Timelines":[{"Items":[{"FilePath":"private-captions.txt"}]}]}"""u8.ToArray());
                await CommitFileAsync(source, "private-captions.txt", "a caption is still an asset"u8.ToArray());
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses renamed registered asset by full SHA-256", () =>
            TestAsync(root, "hash", async (source, git) =>
            {
                var bytes = "known asset registered outside Git"u8.ToArray();
                git.SetPublicationAssets(["voice.txt"], [Convert.ToHexString(SHA256.HashData(bytes))]);
                await CommitFileAsync(source, "newname.txt", bytes);
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses packaged and metadata-identified payloads", async () =>
        {
            await TestAsync(root, "package", async (source, _) =>
            {
                await CommitFileAsync(source, "material.ymmpx", "synthetic package marker"u8.ToArray());
                return (false, null);
            });
            await TestAsync(root, "manifest", async (source, _) =>
            {
                var asset = "a pool asset with a different checkout name"u8.ToArray();
                var sha = Convert.ToHexString(SHA256.HashData(asset));
                await CommitFileAsync(source, "asset-pool.json",
                    Encoding.UTF8.GetBytes($$"""{"SchemaVersion":1,"OriginalFileName":"one.txt","DistributionFileName":"one_hash.txt","Sha256":"{{sha}}"}"""));
                await CommitFileAsync(source, "copy.txt", asset);
                return (false, null);
            });
        });
    }

    private static async Task TestAsync(string root, string name, Func<string, GitRepository, Task<(bool Allow, string? ForbiddenMarker)>> prepare)
    {
        var folder = Path.Combine(root, "pub-" + name);
        Directory.CreateDirectory(folder);
        await CommandAsync(folder, "init", "-b", "main", "src");
        await CommandAsync(folder, "init", "--bare", "out");
        var source = Path.Combine(folder, "src");
        var remote = Path.Combine(folder, "out");
        await CommandAsync(source, "config", "--local", "user.name", "Synthetic");
        await CommandAsync(source, "config", "--local", "user.email", "synthetic@example.invalid");
        await CommandAsync(source, "remote", "add", "origin", remote);
        var git = new GitRepository(source);
        var expected = await prepare(source, git);
        var approval = (await git.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget
            ?? throw new InvalidOperationException("Push target unavailable.");
        var result = await git.PushTrustedAsync(approval);
        var branch = Path.Combine(remote, "refs", "heads", "main");
        if (expected.Allow ? !result.Success || File.ReadAllText(branch).Trim() != approval.ObjectId
            : result.Failure != GitFailure.MediaPublication || File.Exists(branch))
            throw new InvalidOperationException($"Publication expectation failed ({name}): {result.Failure}, {result.StandardError}");
        if (expected.ForbiddenMarker is { } marker && File.Exists(marker))
            throw new InvalidOperationException("Push executed a repository-controlled helper.");
    }

    private static async Task CommitFileAsync(string folder, string relative, byte[] contents)
    {
        await File.WriteAllBytesAsync(Path.Combine(folder, relative), contents);
        await CommandAsync(folder, "add", "--", relative);
        await CommandAsync(folder, "commit", "-m", "synthetic input");
    }

    private static async Task CommandAsync(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var variable in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(variable);
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[] { "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgSign=false" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), output, error);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Synthetic Git fixture failed: {await error}");
    }
}
