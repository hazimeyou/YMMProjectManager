using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitRepository,Gitリポジトリ操作
public sealed partial class GitRepository
{
    //CSJ,method=ResolveRemoteGitExecutable,リモートGit実行ファイルを解決する
    private string ResolveRemoteGitExecutable()
    {
        //CSJ,variable=machine,端末
        var machine = Path.Combine(MachineGitInstallation(), "mingw64", "bin", "git.exe");
        if (Path.IsPathFullyQualified(machine) && IsOrdinaryDirectory(Path.GetDirectoryName(machine)!) && IsRegularFile(machine))
            return machine;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            //CSJ,variable=path,パス
            var path = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var candidate = Path.GetFullPath(Path.Combine(path, "git.exe"));
            //CSJ,variable=relative,相対
            var relative = Path.GetRelativePath(directory, candidate);
            if (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative)) continue;
            if (IsOrdinaryDirectory(path) && IsRegularFile(candidate)) return candidate;
        }
        throw new InvalidOperationException("Git was not found in an absolute PATH directory outside the working repository.");
    }

    //CSJ,method=SetTransportEnvironment,通信環境を設定する
    internal static void SetTransportEnvironment(IDictionary<string, string?> environment, string? allowedProtocol)
    {
        // Partial clones can fetch missing objects even during object lookups such as
        // rev-parse. A local read or consent preview must not initiate network access.
        environment["GIT_NO_LAZY_FETCH"] = "1";
        environment.TryGetValue("GIT_ALLOW_PROTOCOL", out var parentProtocols);
        environment["GIT_ALLOW_PROTOCOL"] = allowedProtocol is not null &&
            (parentProtocols is null || parentProtocols.Split(':').Contains(allowedProtocol, StringComparer.Ordinal))
            ? allowedProtocol : string.Empty;
    }

    //CSJ,method=RunAsync,実行する（非同期）
    private async Task<GitResult> RunAsync(IReadOnlyCollection<string> arguments, CancellationToken token, string? allowedProtocol = null, bool trustedProcess = false,
        bool ignoreLegacyGrafts = false, string[]? pullSettings = null, Action<Process>? afterStart = null, string? standardInput = null,
        //CSJ,parameter=outputLimit,出力上限
        int? outputLimit = null, string? privateIndex = null, Func<Stream, CancellationToken, Task>? rawOutput = null,
        bool noLazyFetch = false, GitHubTransportProfile? githubTransport = null, bool preserveReceiverHooks = false,
        Func<Process, CancellationToken, Task<(string Text, bool Truncated)>>? interaction = null)
    {
        token.ThrowIfCancellationRequested();
        if ((interaction is not null && (rawOutput is not null || standardInput is not null)))
            throw new ArgumentException("Interactive Git commands must own their input and output.");
        var start = new ProcessStartInfo(githubTransport?.Executable ??
            (allowedProtocol is null && !trustedProcess ? "git" : ResolveRemoteGitExecutable()))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = standardInput is not null || interaction is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Git for Windows emits UTF-8 paths in NUL-terminated output.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        //CSJ,variable=name,名前
        foreach (var name in new[]
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_NAMESPACE", "GIT_REPLACE_REF_BASE",
            "GIT_SHALLOW_FILE", "GIT_GRAFT_FILE", "GIT_QUARANTINE_PATH", "GIT_PREFIX", "GIT_IMPLICIT_WORK_TREE",
        })
            start.Environment.Remove(name);
        // Inherited command-scoped config can inject hooks or rewrite a remote independently
        // of the repository and normal user Git configuration that the UI asks users to trust.
        //CSJ,variable=name,名前
        foreach (var name in start.Environment.Keys.Where(name =>
            name.Equals("GIT_CONFIG_COUNT", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("GIT_CONFIG_PARAMETERS", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        if (pullSettings is not null)
        {
            //CSJ,variable=name,名前
            foreach (var name in start.Environment.Keys.Where(name =>
                (name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) &&
                 !name.Equals("GIT_ALLOW_PROTOCOL", StringComparison.OrdinalIgnoreCase)) ||
                name.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SSLKEYLOGFILE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CURL_CA_BUNDLE", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("SSL_CERT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
            if (!preserveReceiverHooks)
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("core.hooksPath=/dev/null");
            }
            //CSJ,variable=argument,引数
            foreach (var argument in new[]
            {
                "--no-pager", "-c", "core.attributesFile=NUL", "-c", "core.askPass=", "-c", "credential.helper=",
                "-c", "core.protectNTFS=true", "-c", "submodule.recurse=false", "-c", "gc.auto=0",
                "-c", "advice.graftFileDeprecated=false",
            }.Concat(pullSettings))
                start.ArgumentList.Add(argument);
        }
        // A graft can appear after the filesystem observation. Ancestry checks always use
        // the real commit objects, even while that observation is being revalidated.
        if (ignoreLegacyGrafts || pullSettings is not null) start.Environment["GIT_GRAFT_FILE"] = "NUL";
        if (noLazyFetch) start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        if (privateIndex is not null) start.Environment["GIT_INDEX_FILE"] = privateIndex;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["LC_ALL"] = "C";
        SetTransportEnvironment(start.Environment, allowedProtocol);
        if (allowedProtocol is not null || trustedProcess)
        {
            //CSJ,variable=name,名前
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GIT_CURL_VERBOSE", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.longpaths=true");
        }
        if (allowedProtocol is not null)
        {
            start.Environment.Remove("GIT_EXEC_PATH");
            start.Environment.Remove("GIT_SSL_NO_VERIFY");
            start.Environment.Remove("GIT_ASKPASS");
            start.Environment.Remove("SSH_ASKPASS");
        }
        githubTransport?.Configure(start.Environment);
        // Status refresh must not silently start a repository-configured fsmonitor process.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.fsmonitor=false");
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        //CSJ,variable=process,プロセス
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        //CSJ,method=ReadOutputAsync,出力を読み取る（非同期）
        async Task<(string Text, bool Truncated)> ReadOutputAsync()
        {
            if (interaction is not null) return await interaction(process, deadline.Token).ConfigureAwait(false);
            if (rawOutput is null) return await ReadGitTextAsync(process.StandardOutput, outputLimit, deadline.Token).ConfigureAwait(false);
            // Closing the pipe on a parse error also releases a child blocked writing output.
            //CSJ,variable=stream,ストリーム
            using var stream = process.StandardOutput.BaseStream;
            await rawOutput(stream, deadline.Token).ConfigureAwait(false);
            return ("", false);
        }
        var output = ReadOutputAsync();
        //CSJ,variable=error,エラー
        var error = ReadGitTextAsync(process.StandardError, outputLimit, deadline.Token);
        var input = standardInput is null ? Task.CompletedTask : WriteGitInputAsync(process, standardInput, deadline.Token);
        try
        {
            afterStart?.Invoke(process);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error, input).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Git operation timed out.");
        }
        //CSJ,variable=stdout,標準出力
        var stdout = await output.ConfigureAwait(false);
        //CSJ,variable=stderr,標準エラー
        var stderr = await error.ConfigureAwait(false);
        return stdout.Truncated || stderr.Truncated
            ? UnsafeRemote("Git output exceeds the supported inspection size.")
            : new GitResult(process.ExitCode, stdout.Text, stderr.Text);
    }

    //CSJ,method=ReadGitTextAsync,Gitテキストを読み取る（非同期）
    private static async Task<(string Text, bool Truncated)> ReadGitTextAsync(StreamReader reader, int? limit,
        CancellationToken token)
    {
        if (limit is null) return (await reader.ReadToEndAsync(token).ConfigureAwait(false), false);

        //CSJ,variable=buffer,バッファー
        var buffer = new char[8192];
        //CSJ,variable=content,内容
        var content = new StringBuilder();
        var truncated = false;
        //CSJ,variable=read,読み取り
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            //CSJ,variable=available,利用可能
            var available = Math.Min(read, limit.Value - content.Length);
            content.Append(buffer, 0, available);
            // Drain the rest so Git can still exit. A truncated observation cannot authorize a write.
            truncated |= available != read;
        }
        return (content.ToString(), truncated);
    }

    //CSJ,method=WriteGitInputAsync,Git入力を書き込む（非同期）
    private static async Task WriteGitInputAsync(Process process, string input, CancellationToken token)
    {
        try { await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(input), token).ConfigureAwait(false); }
        finally { process.StandardInput.Close(); }
    }
}
