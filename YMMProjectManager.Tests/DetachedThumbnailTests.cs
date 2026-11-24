using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;

internal static class DetachedThumbnailTests
{
    internal static async Task RunAsync()
    {
        await Program.RunNamedCaseAsync("Detached thumbnails span the first and last frame with at most 64 draws", () =>
            WithFixtureAsync(async (project, cache, generator, original) =>
            {
                var source = new Source(original, 101);
                var result = await GenerateAsync(project, cache, generator, source);
                Ensure(result.Success && result.SlotsTotal == 64 && result.RenderedCount == 64, result.Reason);
                Ensure(source.Frames.Count == 64 && source.Frames[0] == 0 && source.Frames[^1] == 100 &&
                    source.Frames[32] == 50 && source.Frames.SequenceEqual(source.Frames.Distinct().Order()),
                    "sampling did not cover the ordered video range");
                Ensure(source.DisposeCount == 1 && Directory.GetFiles(result.CacheDirectory, "*.png").Length == 64 &&
                    File.Exists(Path.Combine(result.CacheDirectory, "complete")), "complete strip was not published");
            }));

        await Program.RunNamedCaseAsync("Detached short thumbnails draw each frame once and publish all slots", () =>
            WithFixtureAsync(async (project, cache, generator, original) =>
            {
                var source = new Source(original, 3);
                var result = await GenerateAsync(project, cache, generator, source);
                Ensure(result.Success && result.RenderedCount == 3 && source.Frames.SequenceEqual([0, 1, 2]), result.Reason);
                Ensure(File.ReadAllBytes(Path.Combine(result.CacheDirectory, "000.png")).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(result.CacheDirectory, "030.png"))),
                    "duplicate slots did not reuse identical pixels");
                Ensure(!File.ReadAllBytes(Path.Combine(result.CacheDirectory, "000.png")).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(result.CacheDirectory, "063.png"))),
                    "the final frame had the first frame's pixels");
                Ensure(Path.GetDirectoryName(result.CacheDirectory) == Path.Combine(cache,
                    FilmstripCacheKeyFactory.CreateDetachedHash(project, original)), "cache did not bind to the verified PF bytes");
            }));

        await Program.RunNamedCaseAsync("Detached thumbnail rejects a different loaded PF and incorrect frame", () =>
            WithFixtureAsync(async (project, cache, generator, original) =>
            {
                var other = new Source(original with { FileId = Guid.NewGuid() }, 3);
                var result = await GenerateAsync(project, cache, generator, other);
                Ensure(!result.Success && other.Frames.Count == 0 && other.DisposeCount == 1, "a different PF reached drawing");
                var oldContent = new Source(original with { Sha256 = new string('0', 64) }, 3);
                result = await GenerateAsync(project, cache, generator, oldContent);
                Ensure(!result.Success && oldContent.Frames.Count == 0 && oldContent.DisposeCount == 1,
                    "matching file identity was accepted with stale PF bytes");
                var wrong = new Source(original, 3) { ObservedFrameOffset = 1 };
                result = await GenerateAsync(project, cache, generator, wrong);
                Ensure(!result.Success && wrong.Frames.Count == 1 && wrong.DisposeCount == 1, "the wrong frame reached publication");
                EnsureNoPublication(cache);
            }));

        await Program.RunNamedCaseAsync("Detached thumbnail rejects expired selection and source disposal failure", () =>
            WithFixtureAsync(async (project, cache, generator, original) =>
            {
                var selected = true;
                var source = new Source(original, 2) { AfterDisposal = () => selected = false };
                var result = await GenerateAsync(project, cache, generator, source, () => selected);
                Ensure(!result.Success && source.DisposeCount == 1, "a changed selection reached publication");
                var failing = new Source(original, 2) { AfterDisposal = () => throw new IOException("fixture failure") };
                result = await GenerateAsync(project, cache, generator, failing);
                Ensure(!result.Success && failing.DisposeCount == 1, "source cleanup failure was treated as success");
                EnsureNoPublication(cache);
            }));

        await Program.RunNamedCaseAsync("Detached thumbnail cancellation drops partial output and disposes the source", () =>
            WithFixtureAsync(async (project, cache, generator, original) =>
            {
                using var cancellation = new CancellationTokenSource();
                var source = new Source(original, 101) { AfterDraw = frame => { if (frame > 1) cancellation.Cancel(); } };
                try
                {
                    await GenerateAsync(project, cache, generator, source, token: cancellation.Token);
                    throw new InvalidOperationException("cancellation was lost");
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token) { }
                Ensure(source.Frames.Count > 1 && source.DisposeCount == 1, "source lifetime did not end after partial capture");
                EnsureNoPublication(cache);
            }));
    }

    private static Task<SeekPreviewThumbnailGenerationResult> GenerateAsync(string project, string cache,
        SeekPreviewThumbnailGenerator generator, Source source, Func<bool>? selected = null, CancellationToken token = default) =>
        generator.GenerateDetachedAsync(project, _ => ValueTask.FromResult<IDetachedThumbnailSource>(source),
            selected ?? (() => true), cache, token);

    private static async Task WithFixtureAsync(Func<string, string, SeekPreviewThumbnailGenerator, ProjectFileIdentity, Task> test)
    {
        var fixture = Directory.CreateTempSubdirectory("ymmpm-r32-detached-").FullName;
        try
        {
            var project = Path.Combine(fixture, "project.ymmp");
            File.WriteAllText(project, "synthetic project");
            var original = await ProjectFileEvidence.ReadAsync(project);
            var logger = new FileLogger(Directory.CreateDirectory(Path.Combine(fixture, "no-file.log")).FullName);
            var generator = new SeekPreviewThumbnailGenerator(logger, new CurrentPreviewCaptureService(logger));
            await test(project, Path.Combine(fixture, "cache"), generator, original);
            Ensure(await ProjectFileEvidence.ReadAsync(project) == original, "PF bytes or physical identity changed");
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void EnsureNoPublication(string cache)
    {
        Ensure(!Directory.EnumerateFiles(cache, "complete", SearchOption.AllDirectories).Any() &&
            !Directory.EnumerateDirectories(cache, ".capture-pending-*", SearchOption.AllDirectories).Any(),
            "unsuccessful draw left a completed or owned staging directory");
    }

    private static void Ensure(bool ok, string detail)
    {
        if (!ok)
            throw new InvalidOperationException(detail);
    }

    private sealed class Source(ProjectFileIdentity version, int length) : IDetachedThumbnailSource
    {
        public ProjectFileIdentity ProjectVersion => version;
        public int Length => length;
        internal int ObservedFrameOffset { get; init; }
        internal Action<int>? AfterDraw { get; init; }
        internal Action? AfterDisposal { get; init; }
        internal List<int> Frames { get; } = [];
        internal int DisposeCount { get; private set; }

        public bool IsCurrent() => DisposeCount == 0;

        public ValueTask<DetachedThumbnailFrame> CaptureAsync(int frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Frames.Add(frame);
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { (byte)frame, 0, 0, 255 }, 4);
            image.Freeze();
            AfterDraw?.Invoke(frame);
            return ValueTask.FromResult(new DetachedThumbnailFrame(frame + ObservedFrameOffset, image));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            AfterDisposal?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
