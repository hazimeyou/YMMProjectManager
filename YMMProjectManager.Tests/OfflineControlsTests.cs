using System.IO;
using System.Reflection;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Git;
using YMMProjectManager.Presentation.ViewModels;

internal static class OfflineControlsTests
{
    public static async Task RunAsync(string workRoot)
    {
        await Program.WpfTestHost.RunAsync(async () =>
        {
            var root = Path.Combine(workRoot, "offline-controls"); Directory.CreateDirectory(root);
            var project = Path.Combine(root, "project.ymmp");
            await File.WriteAllTextAsync(project, "{\"Items\":[{\"FilePath\":\"sample.png\"}],\"Other\":{\"FilePath\":\"note.txt\"}}");
            await File.WriteAllTextAsync(Path.Combine(root, "sample.png"), "synthetic");
            var git = new GitRepository(root);
            if (!(await git.InitializeAsync()).Success) throw new Exception("git init failed");
            var vm = new ProjectListViewModel(new FileLogger(Path.Combine(root, "test.log")), null);
            vm.SelectedProject = new ProjectEntry { FullPath = project };
            async Task Invoke(string name, params object[] args) => await (Task)typeof(ProjectListViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!;
            await Invoke("ShowGitStatusAsync", false);
            if (!vm.GitStatus.Contains("Git:") || !vm.GitStatus.Contains("project.ymmp")) throw new Exception("Git status not displayed");
            await Invoke("FindAssetCandidatesAsync");
            if (!vm.AssetStatus.Contains("素材とは限りません") || !vm.AssetStatus.Contains("sample.png") || !vm.AssetStatus.Contains("note.txt")) throw new Exception("candidate labels missing");
            vm.AssetPath = "sample.png";
            await Invoke("InspectAssetAsync");
            if (!vm.AssetStatus.Contains("SHA-256:") || !vm.AssetStatus.Contains("9 bytes")) throw new Exception("asset inspection missing");
            vm.SelectedProject = null;
            if (vm.AssetStatus != string.Empty || vm.GitStatus != string.Empty) throw new Exception("stale status after selection");
            Console.WriteLine("Offline Git and asset ViewModel checks passed.");
            return true;
        });
    }
}
