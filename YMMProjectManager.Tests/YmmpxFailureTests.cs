using System.IO;
using System.IO.Compression;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

internal static class YmmpxFailureTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "ymmpx-failure"); Directory.CreateDirectory(root);
        await Program.RunNamedCaseAsync("Owned staging directory publishes only under its reserved parent", () =>
        {
            var elsewhere = Path.Combine(root, "other-parent");
            Directory.CreateDirectory(elsewhere);
            using var reserved = OwnedStagingDirectory.Reserve(root, ".stage-");
            try
            {
                reserved.Publish(Path.Combine(elsewhere, "unexpected"), default);
                throw new InvalidOperationException("Staging tried to publish outside its reserved parent.");
            }
            catch (ArgumentException) { }
            if (reserved.CheckRecoveryPath() != reserved.Path || !reserved.DeleteIfEmpty())
                throw new InvalidOperationException("Rejected destination changed the reserved stage.");
            return Task.CompletedTask;
        });
        var project = Path.Combine(root, "sample.ymmp"); var output = Path.Combine(root, "sample.ymmpx");
        await File.WriteAllTextAsync(project, "{}", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await File.WriteAllTextAsync(output, "original package");
        var bridge = new YmmpxLibBridge(new FileLogger(Path.Combine(root, "test.log")), typeof(YmmpxLib.YmmpxPackageService).Assembly);
        var refusal = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
        if (refusal.Success || !refusal.ErrorMessage!.Contains("別の名前") || await File.ReadAllTextAsync(output) != "original package")
            throw new Exception("existing package was not protected");
        var newPath = Path.Combine(root, "new.ymmpx");
        var result = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (result.Success || File.Exists(newPath)) throw new Exception("partial package was published");
        YmmpxLib.YmmpxPackageService.Mode = "invalid";
        var invalid = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (invalid.Success || File.Exists(newPath)) throw new Exception("invalid archive was published");
        YmmpxLib.YmmpxPackageService.Mode = "unsafe";
        var unsafeArchive = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (unsafeArchive.Success || File.Exists(newPath)) throw new Exception("unsafe archive was published");
        YmmpxLib.YmmpxPackageService.Mode = "missing-marker";
        var missingMarker = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (missingMarker.Success || File.Exists(newPath)) throw new Exception("package without project marker was published");
        YmmpxLib.YmmpxPackageService.Mode = "missing-project";
        var missingProject = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (missingProject.Success || File.Exists(newPath)) throw new Exception("package with dangling project marker was published");
        YmmpxLib.YmmpxPackageService.Mode = "missing-links";
        var missingLinks = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (missingLinks.Success || File.Exists(newPath)) throw new Exception("package without links manifest was published");
        YmmpxLib.YmmpxPackageService.Mode = "oversized-marker";
        var oversizedMarker = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (oversizedMarker.Success || File.Exists(newPath)) throw new Exception("package with oversized marker was published");
        foreach (var mode in new[] { "invalid-project-json", "invalid-links-json" })
        {
            YmmpxLib.YmmpxPackageService.Mode = mode;
            if ((await bridge.CreatePackageAsync(project, newPath, CancellationToken.None)).Success || File.Exists(newPath))
                throw new Exception("package with malformed JSON was published: " + mode);
        }
        await Program.RunNamedCaseAsync("Packaging refuses an archive for another project", async () =>
        {
            const string mode = "other-project";
            YmmpxLib.YmmpxPackageService.Mode = mode;
            var wrongProject = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
            if (wrongProject.Success || File.Exists(newPath))
                throw new InvalidOperationException("Published an archive for a different project.");
        });
        await Program.RunNamedCaseAsync("Packaging refuses a different project with the same name", async () =>
        {
            var selected = Path.Combine(root, "same.ymmp");
            const string original = """{"Title":"Selected","Items":[{"FilePath":"original.wav"}]}""";
            await File.WriteAllTextAsync(selected, original);
            var destination = Path.Combine(root, "same-name-output.ymmpx");
            YmmpxLib.YmmpxPackageService.Mode = "same-name-other-project";
            try
            {
                var other = await bridge.CreatePackageAsync(selected, destination, default);
                if (other.Success || File.Exists(destination) || await File.ReadAllTextAsync(selected) != original)
                    throw new InvalidOperationException("Same-name archive changed the selected project or reached the destination.");
            }
            finally { YmmpxLib.YmmpxPackageService.Mode = "success"; }
        });
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await bridge.CreatePackageAsync(project, newPath, cancelled.Token); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { }
            if (File.Exists(newPath)) throw new Exception("cancelled operation created package");
            var extractPath = Path.Combine(root, "cancelled-extraction");
            var calls = YmmpxLib.YmmpxPackageService.ExtractionCalls;
            try { await bridge.ExtractPackageAsync(output, extractPath, cancelled.Token); throw new Exception("extraction cancellation ignored"); } catch (OperationCanceledException) { }
            if (Directory.Exists(extractPath) || YmmpxLib.YmmpxPackageService.ExtractionCalls != calls) throw new Exception("cancelled extraction created output or called extractor");
        }
        YmmpxLib.YmmpxPackageService.Mode = "success";
        var missingOutput = Path.Combine(root, "missing-source.ymmpx");
        var missingSource = await bridge.CreatePackageAsync(Path.Combine(root, "missing.ymmp"), missingOutput, CancellationToken.None);
        if (missingSource.Success || File.Exists(missingOutput)) throw new Exception("package creation accepted missing project");
        var malformedSource = Path.Combine(root, "malformed.ymmp");
        var invocationCount = 0;
        YmmpxLib.YmmpxPackageService.DuringPackaging = _ => invocationCount++;
        foreach (var invalidJson in new[] { "{", "[]" })
        {
            await File.WriteAllTextAsync(malformedSource, invalidJson);
            if ((await bridge.CreatePackageAsync(malformedSource, missingOutput, CancellationToken.None)).Success ||
                File.Exists(missingOutput) || invocationCount != 0)
                throw new Exception("malformed project reached the library or published output");
        }
        YmmpxLib.YmmpxPackageService.DuringPackaging = null;
        using (var interrupted = new CancellationTokenSource())
        {
            YmmpxLib.YmmpxPackageService.Mode = "cancel-after-write";
            YmmpxLib.YmmpxPackageService.AfterPartialWrite = interrupted.Cancel;
            try
            {
                await bridge.CreatePackageAsync(project, missingOutput, interrupted.Token);
                throw new Exception("packaging cancellation after partial output was ignored");
            }
            catch (OperationCanceledException) { }
            finally
            {
                YmmpxLib.YmmpxPackageService.AfterPartialWrite = null;
                YmmpxLib.YmmpxPackageService.Mode = "success";
            }
            if (File.Exists(missingOutput) || Directory.EnumerateDirectories(root, ".ymmpm-package-*").Any() ||
                await File.ReadAllTextAsync(output) != "original package")
                throw new Exception("cancelled creation left partial output or changed an existing file");
        }
        await Program.RunNamedCaseAsync("Cancelled packaging preserves a replacement at its temporary name", async () =>
        {
            var isolated = Path.Combine(root, "retained-replacement");
            Directory.CreateDirectory(isolated);
            var destination = Path.Combine(isolated, "cancel-replaced.ymmpx");
            var movedOriginal = Path.Combine(isolated, "moved-partial.ymmpx");
            string? replacedPath = null;
            using var cancelled = new CancellationTokenSource();
            YmmpxLib.YmmpxPackageService.Mode = "cancel-after-write";
            YmmpxLib.YmmpxPackageService.AfterPartialWrite = () =>
            {
                var staging = Directory.EnumerateDirectories(isolated, ".ymmpm-package-*").Single();
                replacedPath = Directory.EnumerateFiles(staging, ".cancel-replaced.*.tmp.ymmpx").Single();
                File.Move(replacedPath, movedOriginal);
                File.WriteAllText(replacedPath, "separate file");
                cancelled.Cancel();
            };
            try
            {
                await bridge.CreatePackageAsync(project, destination, cancelled.Token);
                throw new InvalidOperationException("Expected the packaging attempt to be cancelled.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            finally
            {
                YmmpxLib.YmmpxPackageService.AfterPartialWrite = null;
                YmmpxLib.YmmpxPackageService.Mode = "success";
            }
            if (replacedPath is null || await File.ReadAllTextAsync(replacedPath) != "separate file" ||
                await File.ReadAllTextAsync(movedOriginal) != "partial" || File.Exists(destination))
                throw new InvalidOperationException("Cancellation removed a file that the active operation did not own.");
        });
        YmmpxLib.YmmpxPackageService.ExternalPath = newPath;
        var conflict = await bridge.CreatePackageAsync(project, newPath, CancellationToken.None);
        if (conflict.Success || await File.ReadAllTextAsync(newPath) != "external edit")
            throw new Exception("concurrent destination overwritten");
        YmmpxLib.YmmpxPackageService.ExternalPath = null;
        var safePath = Path.Combine(root, "save-as.ymmpx");
        var sourceWriteDenied = false;
        YmmpxLib.YmmpxPackageService.DuringPackaging = path =>
        {
            try { File.WriteAllText(path, "external edit"); }
            catch (IOException) { sourceWriteDenied = true; }
        };
        var created = await bridge.CreatePackageAsync(project, safePath, CancellationToken.None);
        YmmpxLib.YmmpxPackageService.DuringPackaging = null;
        if (!created.Success || created.OutputPath != safePath || !IsPackage(safePath)) throw new Exception("new package failed");
        if (!sourceWriteDenied || await File.ReadAllTextAsync(project) != "{}") throw new Exception("project changed during packaging");
        using (var held = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var locked = await bridge.CreatePackageAsync(project, output, CancellationToken.None);
            if (locked.Success) throw new Exception("locked package replaced");
        }
        if (await File.ReadAllTextAsync(output) != "original package") throw new Exception("existing package changed");
        if (Directory.EnumerateDirectories(root, ".ymmpm-package-*").Any()) throw new Exception("temporary package remained");
        var generated = Path.Combine(root, "generated.tmp");
        var existingOutput = Path.Combine(root, "existing.ymmpx");
        await File.WriteAllTextAsync(generated, "synthetic new package");
        await File.WriteAllTextAsync(existingOutput, "synthetic existing package");
        try { PackagingOutputHelper.MoveGeneratedPackage(generated, existingOutput); throw new Exception("existing package was overwritten by helper"); }
        catch (IOException) { }
        if (await File.ReadAllTextAsync(existingOutput) != "synthetic existing package" || !File.Exists(generated)) throw new Exception("existing package or generated source was changed");
        Console.WriteLine("YMMPX synthetic partial write preserves existing package.");
    }

    private static bool IsPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.GetEntry("resources/synthetic.txt") is not null;
    }
}

namespace YmmpxLib
{
    public sealed class YmmpxPackagingOptions { public bool IncludeProjectUiSettings { get; set; } }
    public sealed class YmmpxPackagingProgress { public int Percentage { get; set; } }
    public static class YmmpxPackageService
    {
        public static string Mode { get; set; } = "failure";
        public static string? ExternalPath { get; set; }
        public static Action<string>? DuringPackaging { get; set; }
        public static Action? AfterPartialWrite { get; set; }
        public static int ExtractionCalls { get; private set; }
        public static string GetAvailableDirectoryPath(string path) => path;
        public static object ExtractAndRestoreProject(string path, string destination)
        {
            ExtractionCalls++;
            throw new InvalidOperationException("synthetic extraction must not be called for unsafe archives");
        }
        public static async Task<object> CreatePackageAsync(string project, string output, HashSet<string> ignored, YmmpxPackagingOptions options, IProgress<YmmpxPackagingProgress>? progress, CancellationToken token)
        {
            DuringPackaging?.Invoke(project);
            if (Mode == "cancel-after-write")
            {
                await File.WriteAllTextAsync(output, "partial", token);
                AfterPartialWrite?.Invoke();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("cancellation fixture was not configured");
            }
            if (Mode == "failure")
            {
                await File.WriteAllTextAsync(output, "partial", token);
                throw new IOException("synthetic partial write");
            }
            if (Mode == "invalid")
            {
                await File.WriteAllTextAsync(output, "invalid archive", token);
                return new { OutputPath = output, ResourceCount = 0 };
            }
            var projectName = Mode == "other-project" ? "other-project.ymmp" : Path.GetFileName(project);
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry(Mode == "unsafe" ? "../escape.txt" : "resources/synthetic.txt").Open()))
                    await writer.WriteAsync("synthetic package");
                if (Mode != "missing-marker")
                    using (var writer = new StreamWriter(archive.CreateEntry("_ymmpx_project_path.txt").Open())) await writer.WriteAsync(Mode == "oversized-marker" ? new string('x', 4097) : projectName);
                if (Mode != "missing-project")
                {
                    var document = Mode switch
                    {
                        "invalid-project-json" => "{",
                        "same-name-other-project" => """{"Title":"Different","Items":[{"FilePath":"resources/synthetic.txt"}]}""",
                        _ => await File.ReadAllTextAsync(project, token),
                    };
                    using var writer = new StreamWriter(archive.CreateEntry(projectName).Open());
                    await writer.WriteAsync(document.AsMemory(), token);
                }
                if (Mode != "missing-links")
                    using (var writer = new StreamWriter(archive.CreateEntry("links.json").Open())) await writer.WriteAsync(Mode == "invalid-links-json" ? "[]" : """{"resources/synthetic.txt":"resources/synthetic.txt"}""");
            }
            if (ExternalPath is not null) await File.WriteAllTextAsync(ExternalPath, "external edit", token);
            return new { OutputPath = output, ResourceCount = 1 };
        }
    }
}
