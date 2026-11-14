using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Presentation.ViewModels;

internal static class ProjectListRecoveryTests
{
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    public static async Task RunAsync(string workRoot)
    {
        await Program.WpfTestHost.RunAsync(async () =>
        {
            var root = Path.Combine(workRoot, "recovery"); Directory.CreateDirectory(root);
            var list = Path.Combine(root, "projects.json"); var project = Path.Combine(root, "sample.ymmp");
            await File.WriteAllTextAsync(project, "{}");
            var logger = new FileLogger(Path.Combine(root, "test.log"));
            await new JsonProjectRepository(logger, list).SaveAsync(new());
            var before = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(list));
            bool fail = true; bool cancel = false; int calls = 0;
            var repo = new JsonProjectRepository(logger, list, beforeReplace: () => { calls++; if (cancel) throw new OperationCanceledException(); if (fail) throw new IOException("synthetic failure"); });
            var vm = new ProjectListViewModel(logger, repo);
            await vm.InitializeAsync();
            var threadId = GetCurrentThreadId(); int dialogs = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => EnumThreadWindows(threadId, (hwnd, _) => { var name = new StringBuilder(128); GetClassName(hwnd, name, name.Capacity); if (name.ToString() == "#32770") { dialogs++; PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); } return true; }, IntPtr.Zero);
            timer.Start();
            try { await vm.AddProjectsAsync([project]); } finally { timer.Stop(); }
            var afterFailure = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(list));
            if (dialogs == 0 || vm.Projects.Count != 1 || !before.SequenceEqual(afterFailure) ) throw new Exception("failure notification or preservation not observed");
            fail = false;
            if (!vm.HasUnsavedProjectList || !vm.RetryProjectListSaveCommand.CanExecute(null)) throw new Exception("pending state missing");
            await vm.AddProjectsAsync([project]);
            if (calls != 1 || vm.Projects.Count != 1) throw new Exception("duplicate add unexpectedly changed state");
            await vm.RetryProjectListSaveAsync();
            var reloaded = await new JsonProjectRepository(logger, list).LoadAsync();
            if (calls != 2 || vm.HasUnsavedProjectList || reloaded.Projects.Count != 1 || reloaded.Projects[0].FullPath != project) throw new Exception("retry failed");
            // A failed removal keeps the change in memory and can be saved without repeating removal.
            fail = true; var priorDialogs = dialogs; timer.Start();
            try { await (Task)typeof(ProjectListViewModel).GetMethod("RemoveAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null)!; } finally { timer.Stop(); }
            if (dialogs == priorDialogs || vm.Projects.Count != 0 || !vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 1) throw new Exception("remove failure state");
            fail = false; await vm.RetryProjectListSaveAsync();
            if (vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 0) throw new Exception("remove retry");
            // The extracted-project registration must propagate its save exception to its caller.
            var extracted = Path.Combine(root, "extracted.ymmp"); await File.WriteAllTextAsync(extracted, "{}"); fail = true;
            try { await (Task)typeof(ProjectListViewModel).GetMethod("RegisterExtractedProjectAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [extracted])!; throw new Exception("registration swallowed failure"); } catch (IOException) { }
            if (!vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 0) throw new Exception("registration failure state");
            fail = false; await vm.RetryProjectListSaveAsync();
            if ((await repo.LoadAsync()).Projects.Single().FullPath != extracted || vm.HasUnsavedProjectList) throw new Exception("registration retry");
            // Cancellation is not shown as an I/O error and remains retryable.
            cancel = true; var extra = Path.Combine(root,"cancelled.ymmp"); await File.WriteAllTextAsync(extra,"{}"); priorDialogs=dialogs; timer.Start();
            try { await vm.AddProjectsAsync([extra]); } finally { timer.Stop(); }
            if (dialogs != priorDialogs || !vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 1) throw new Exception("cancellation behavior");
            cancel = false; await vm.RetryProjectListSaveAsync();
            if (vm.HasUnsavedProjectList || (await repo.LoadAsync()).Projects.Count != 2) throw new Exception("cancellation retry");
            foreach (var content in new[] { "{invalid", "" })
            {
                var broken = Path.Combine(root, content.Length == 0 ? "empty.json" : "broken.json");
                await File.WriteAllTextAsync(broken, content); var brokenHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(broken));
                var brokenVm = new ProjectListViewModel(logger, new JsonProjectRepository(logger, broken)); priorDialogs=dialogs; timer.Start();
                try { await brokenVm.InitializeAsync(); await brokenVm.AddProjectsAsync([project]); } finally { timer.Stop(); }
                var brokenAfter = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(broken));
                if (dialogs < priorDialogs + 2 || brokenVm.AddCommand.CanExecute(null) || brokenVm.Projects.Count != 0 || !brokenHash.SequenceEqual(brokenAfter)) throw new Exception("failed load protection");
            }
            var lockedPath = Path.Combine(root, "locked.json"); await new JsonProjectRepository(logger, lockedPath).SaveAsync(new());
            var lockedHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(lockedPath));
            var lockedVm = new ProjectListViewModel(logger, new JsonProjectRepository(logger, lockedPath)); priorDialogs=dialogs;
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                timer.Start(); try { await lockedVm.InitializeAsync(); } finally { timer.Stop(); }
            }
            var unlockedHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(lockedPath));
            if (dialogs == priorDialogs || lockedVm.AddCommand.CanExecute(null) || !lockedHash.SequenceEqual(unlockedHash)) throw new Exception("locked load protection");
            Console.WriteLine("R1-A2 independent WPF: add/remove/registration/cancellation and save-only retry passed.");
            Console.WriteLine("R1-A2 isolated original SHA-256 before/after failed add: " + Convert.ToHexString(before) + " / " + Convert.ToHexString(afterFailure));
            Console.WriteLine("R1-A2 independent WPF: invalid, empty and locked load protection passed.");
            return true;
        });
    }
}
