using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Presentation.ViewModels;

internal static class LiveThumbnailSafetyTests
{
    internal static async Task RunAsync()
    {
        await DetachedThumbnailTests.RunAsync();

        await Program.RunNamedCaseAsync("R28 thumbnail publishes a complete strip and restores the host frame", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                var result = await generator.GenerateAsync(project, source, () => true, cacheRoot, CancellationToken.None);
                Ensure(result.Success && result.RenderedCount == 64, result.Reason);
                Ensure(source.Frame == 2 && source.Position == source.TimeAtFrame(2), "original position was not restored");
                Ensure(Directory.GetFiles(result.CacheDirectory, "*.png").Length == 64, "incomplete strip was published");
                Ensure(File.ReadAllText(Path.Combine(legacy, "000.png")) == "previous", "previous thumbnail was replaced");
                Ensure(ThumbnailCachePublication.GetDisplayDirectory(cacheRoot, project) == result.CacheDirectory,
                    "reopened cache did not select the new strip");
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail refuses an unknown owner before seeking or capturing", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                source.Current = false;
                var result = await generator.GenerateAsync(project, source, () => true, cacheRoot, CancellationToken.None);
                Ensure(!result.Success && source.Captures == 0 && source.Seeks == 0, "an unverified owner reached capture");
                EnsurePreserved(cacheRoot, project, legacy);
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail refuses a known owner without proof of the loaded PF", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                source.HasLoadEvidence = false;
                var result = await generator.GenerateAsync(project, source, () => true, cacheRoot, CancellationToken.None);
                Ensure(!result.Success && source.Captures == 0 && source.Seeks == 0, "a matching path was accepted without load evidence");
                EnsurePreserved(cacheRoot, project, legacy);
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail discards a frame after the preview owner changes", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                source.AfterCapture = () => source.Current = false;
                var result = await generator.GenerateAsync(project, source, () => true, cacheRoot, CancellationToken.None);
                Ensure(!result.Success && source.Seeks == 1, "changed owner was moved back or used for publication");
                EnsurePreserved(cacheRoot, project, legacy);
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail cancellation preserves prior output and restores its own frame", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                using var cancel = new CancellationTokenSource();
                source.AfterCapture = cancel.Cancel;
                try
                {
                    await generator.GenerateAsync(project, source, () => true, cacheRoot, cancel.Token);
                    throw new InvalidOperationException("Cancellation was not propagated.");
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == cancel.Token) { }
                Ensure(source.Frame == 2, "cancelled operation did not restore its own frame");
                EnsurePreserved(cacheRoot, project, legacy);
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail rejects a selection change just before publication", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                var sameSelection = true;
                // Frame 2 was the original position and occurs among sampled frames as well.
                source.AfterSeek = () =>
                {
                    if (source.Captures == 64)
                        sameSelection = false;
                };
                var result = await generator.GenerateAsync(project, source, () => sameSelection, cacheRoot, CancellationToken.None);
                Ensure(!result.Success && source.Captures == 64 && source.Frame == 2, "stale selection reached publication");
                EnsurePreserved(cacheRoot, project, legacy);
            }));

        await Program.RunNamedCaseAsync("R29 thumbnail refuses an intermediate selection or path change even if reverted", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                var root = Path.GetDirectoryName(project)!;
                var logger = new FileLogger(root);
                var view = new ProjectListViewModel(logger, new JsonProjectRepository(logger, Path.Combine(root, "list.json")));
                var selected = new ProjectEntry { FullPath = project };
                var other = new ProjectEntry { FullPath = Path.Combine(root, "other.ymmp") };
                view.SelectedProject = selected;
                using (var selection = new ProjectListViewModel.ThumbnailProjectSelection(view, selected))
                {
                    source.AfterSeek = () =>
                    {
                        view.SelectedProject = other;
                        view.SelectedProject = selected;
                    };
                    var result = await generator.GenerateAsync(project, source, selection.IsCurrent, cacheRoot, CancellationToken.None);
                    Ensure(!result.Success && source.Captures == 0 && source.Frame == 2, "a reverted selection change reached capture");
                    EnsurePreserved(cacheRoot, project, legacy);
                }

                using var nextSelection = new ProjectListViewModel.ThumbnailProjectSelection(view, selected);
                source.AfterSeek = () =>
                {
                    selected.FullPath = other.FullPath;
                    selected.FullPath = project;
                };
                var pathChange = await generator.GenerateAsync(project, source, nextSelection.IsCurrent, cacheRoot, CancellationToken.None);
                Ensure(!pathChange.Success && source.Captures == 0 && source.Frame == 2, "a reverted path change reached capture");
                EnsurePreserved(cacheRoot, project, legacy);
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail preserves manual position and rejects overlap", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                var duringSeek = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finishSeek = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                source.SeekPending = async () =>
                {
                    duringSeek.TrySetResult();
                    await finishSeek.Task;
                };
                var first = generator.GenerateAsync(project, source, () => true, cacheRoot, CancellationToken.None);
                await duringSeek.Task;
                try
                {
                    using var concurrent = new Source();
                    var overlap = await generator.GenerateAsync(project, concurrent, () => true, cacheRoot, CancellationToken.None);
                    Ensure(!overlap.Success && concurrent.Seeks == 0, "concurrent operation changed the host");
                    source.MoveTo(3);
                }
                finally { finishSeek.SetResult(); }
                var result = await first;
                Ensure(!result.Success && source.Frame == 3 && source.Seeks == 1, "manual position was overwritten");
                EnsurePreserved(cacheRoot, project, legacy);
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail cache separates identical timestamps on replaced PFs", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                var result = await generator.GenerateAsync(project, source, () => true, cacheRoot, CancellationToken.None);
                Ensure(result.Success, result.Reason);
                var time = File.GetLastWriteTimeUtc(project);
                var original = Path.Combine(Path.GetDirectoryName(project)!, "original.ymmp");
                File.Move(project, original);
                try
                {
                    File.WriteAllText(project, "synthetic replace");
                    File.SetLastWriteTimeUtc(project, time);
                    Ensure(File.GetLastWriteTimeUtc(project) == time && new FileInfo(original).Length == new FileInfo(project).Length,
                        "the replacement fixture did not preserve size and timestamp");
                    Ensure(ThumbnailCachePublication.GetDisplayDirectory(cacheRoot, project) == legacy,
                        "a different physical PF received the verified cache from the original");
                }
                finally
                {
                    File.Delete(project);
                    File.Move(original, project);
                }
                Ensure(ThumbnailCachePublication.GetDisplayDirectory(cacheRoot, project) == result.CacheDirectory,
                    "the original PF lost its verified cache");
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail recovers the last owned position if a seek fails before movement", () =>
            WithFixtureAsync(async (generator, project, cacheRoot, legacy, source) =>
            {
                source.BeforeSeek = () =>
                {
                    if (source.Seeks == 2)
                        throw new IOException("Synthetic seek fault before the position moved.");
                };
                var result = await generator.GenerateAsync(project, source, () => true, cacheRoot, CancellationToken.None);
                Ensure(!result.Success && source.Seeks == 3 && source.Frame == 2, "last confirmed position was not restored");
                EnsurePreserved(cacheRoot, project, legacy);
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail detects staged bytes modified before directory publication", () =>
            WithFixtureAsync((_, project, cacheRoot, legacy, _) =>
            {
                using (var cache = StageStrip(cacheRoot, project, out var directory))
                {
                    try
                    {
                        cache.Publish(CancellationToken.None, () =>
                        {
                            var staged = Directory.EnumerateDirectories(directory, ".capture-pending-*").Single();
                            File.WriteAllBytes(Path.Combine(staged, "000.png"), [99]);
                        });
                        throw new InvalidOperationException("modified staged bytes reached publication");
                    }
                    catch (IOException) { }
                }
                EnsurePreserved(cacheRoot, project, legacy);
                return Task.CompletedTask;
            }));

        await Program.RunNamedCaseAsync("R28 thumbnail pins verified frames through completion and hides cancelled publication", () =>
            WithFixtureAsync((_, project, cacheRoot, legacy, _) =>
            {
                using var cache = StageStrip(cacheRoot, project, out var directory);
                using var cancel = new CancellationTokenSource();
                var callbackCount = 0;
                try
                {
                    cache.Publish(cancel.Token, () =>
                    {
                        if (++callbackCount != 2)
                            return;
                        var pending = Directory.EnumerateDirectories(directory, "capture-*").Single();
                        try
                        {
                            File.WriteAllBytes(Path.Combine(pending, "000.png"), [99]);
                            throw new InvalidOperationException("an already verified frame could be edited before the marker");
                        }
                        catch (IOException) { }
                        cancel.Cancel();
                    });
                    throw new InvalidOperationException("cancelled publication returned success");
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == cancel.Token) { }
                Ensure(callbackCount == 2 && Directory.GetFiles(directory, "complete", SearchOption.AllDirectories).Length == 0,
                    "cancelled generation received a completion marker");
                Ensure(ThumbnailCachePublication.GetDisplayDirectory(cacheRoot, project) == legacy,
                    "an incomplete publication hid the previous strip");
                return Task.CompletedTask;
            }));
    }

    private static ThumbnailCachePublication StageStrip(string cacheRoot, string project, out string directory)
    {
        using var file = OwnedFilePublication.OpenOrdinaryPath(project, expectDirectory: false, allowRename: false);
        directory = Path.Combine(cacheRoot, FilmstripCacheKeyFactory.CreateLiveHash(project, file));
        var cache = new ThumbnailCachePublication(directory);
        try
        {
            for (var slot = 0; slot < ThumbnailCachePublication.SlotCount; slot++)
                cache.WriteFrame(slot, stream => stream.WriteByte((byte)slot));
            return cache;
        }
        catch
        {
            cache.Dispose();
            throw;
        }
    }

    private static async Task WithFixtureAsync(
        Func<SeekPreviewThumbnailGenerator, string, string, string, Source, Task> run)
    {
        var root = Directory.CreateTempSubdirectory("r28-th-").FullName;
        using var source = new Source();
        try
        {
            var projectPath = Path.Combine(root, "fixture.ymmp");
            File.WriteAllText(projectPath, "synthetic project");
            var cacheRoot = Path.Combine(root, "filmstrip");
            var legacyDirectory = Path.Combine(cacheRoot, FilmstripCacheKeyFactory.TryCreateHash(projectPath)!);
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(Path.Combine(legacyDirectory, "000.png"), "previous");
            var logPath = Directory.CreateDirectory(Path.Combine(root, "test.log")).FullName;
            var logger = new FileLogger(logPath);
            await run(new SeekPreviewThumbnailGenerator(logger, new CurrentPreviewCaptureService(logger)),
                projectPath, cacheRoot, legacyDirectory, source);
            Ensure(File.ReadAllText(projectPath) == "synthetic project", "PF was modified");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void EnsurePreserved(string cacheRoot, string project, string legacy)
    {
        Ensure(ThumbnailCachePublication.GetDisplayDirectory(cacheRoot, project) == legacy,
            "a changed or incomplete strip became visible");
        Ensure(File.ReadAllText(Path.Combine(legacy, "000.png")) == "previous", "previous thumbnail was changed");
        Ensure(!Directory.EnumerateDirectories(cacheRoot, ".capture-pending-*", SearchOption.AllDirectories).Any(),
            "owned staging survived an unsuccessful capture");
    }

    private static void Ensure(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    private sealed class Source : ILiveThumbnailSource
    {
        private readonly BitmapSource bitmap;
        internal Source()
        {
            bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
            bitmap.Freeze();
        }

        public int Length => 5;
        public int Frame { get; private set; } = 2;
        public TimeSpan Position => TimeAtFrame(Frame);
        public TimeSpan TimeAtFrame(int frame) => TimeSpan.FromMilliseconds(frame * 40);
        public bool IsCurrent() => Current;
        public bool MatchesLoadedProject(SafeFileHandle projectFile) => HasLoadEvidence && !projectFile.IsInvalid && !projectFile.IsClosed;
        internal bool Current { get; set; } = true;
        internal bool HasLoadEvidence { get; set; } = true;
        internal int Captures { get; private set; }
        internal int Seeks { get; private set; }
        internal Action? AfterCapture { get; set; }
        internal Action? BeforeSeek { get; set; }
        internal Action? AfterSeek { get; set; }
        internal Func<Task>? SeekPending { get; set; }

        public async Task SeekAsync(TimeSpan position)
        {
            Seeks++;
            BeforeSeek?.Invoke();
            MoveTo((int)position.TotalMilliseconds / 40);
            AfterSeek?.Invoke();
            if (SeekPending is not null)
                await SeekPending();
        }

        public BitmapSource Capture()
        {
            Captures++;
            AfterCapture?.Invoke();
            return bitmap;
        }

        internal void MoveTo(int frame) => Frame = frame;
        public void Dispose() { }
    }
}
