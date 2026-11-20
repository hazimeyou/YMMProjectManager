using System.Diagnostics;
using System.IO;
using YMMProjectManager.Infrastructure;

internal static class ProjectListWpfFixtureProcess
{
    internal const string ChildArgument = "--project-list-wpf-recovery";
    internal const string StalledChildArgument = "--project-list-wpf-watchdog-fixture";

    internal static Task RunAsync(string workRoot) => RunOwnedAsync(workRoot, ChildArgument, TimeSpan.FromSeconds(60));

    internal static async Task VerifyTimeoutAsync(string workRoot)
    {
        try { await RunOwnedAsync(workRoot, StalledChildArgument, TimeSpan.FromMilliseconds(500)); }
        catch (TimeoutException ex) when (ex.Message.StartsWith("The isolated project-list WPF fixture exceeded", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("The watchdog did not terminate an owned non-completing fixture.");
    }

    private static async Task RunOwnedAsync(string workRoot, string argument, TimeSpan deadline)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(argument);
        start.ArgumentList.Add(workRoot);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The isolated WPF fixture could not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(deadline);
        }
        catch (TimeoutException)
        {
            // The child owns the STA and its modal dialogs. Stop just this fixture so the suite can continue.
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (child.HasExited) { }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await WriteResultsAsync();
            throw new TimeoutException($"The isolated project-list WPF fixture exceeded its deadline of {deadline.TotalSeconds} seconds.");
        }
        await WriteResultsAsync();
        if (child.ExitCode != 0) throw new InvalidOperationException($"The isolated WPF fixture failed with exit code {child.ExitCode}.");

        async Task WriteResultsAsync()
        {
            var result = await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
            Console.Write(result[0]);
            Console.Error.Write(result[1]);
        }
    }

    internal static Task RunChildAsync(string path, bool stalled)
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests")) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(path);
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            throw new InvalidOperationException("The WPF fixture root is outside the isolated test directory.");
        ProjectListDrafts.CheckOrdinaryDirectory(root);
        return stalled ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task : ProjectListRecoveryTests.RunAsync(root);
    }
}
