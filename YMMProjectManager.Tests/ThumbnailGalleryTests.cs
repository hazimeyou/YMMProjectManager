using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;
using YMMProjectManager.Presentation.Thumbnails;

internal static class ThumbnailGalleryTests
{
    internal static async Task RunAsync()
    {
        await Program.RunNamedCaseAsync("R33 cache keeps the project version pinned through display handoff",
            () => WithFixtureAsync(async (project, cache, _, _, _) =>
            {
                var legacy = Path.Combine(cache, FilmstripCacheKeyFactory.TryCreateHash(project)!);
                Directory.CreateDirectory(legacy);
                ThumbnailSequenceFrameRenderer.SaveBlankPng(Path.Combine(legacy, "000.png"));
                var replacement = Path.Combine(Path.GetDirectoryName(project)!, "replacement.ymmp");
                File.WriteAllText(replacement, "{}");
                var adopted = false;
                await ThumbnailGalleryCache.ReadAsync(cache, project, default, (frames, _) =>
                {
                    Ensure(frames.Count == 1, "an unreadable cache did not test the display handoff");
                    try
                    {
                        File.Move(replacement, project, overwrite: true);
                        throw new InvalidOperationException("the PF could be replaced during the display handoff");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    adopted = true;
                    return Task.CompletedTask;
                });
                Ensure(adopted && File.Exists(replacement), "the display handoff was not reached or changed the fixture");
            }));

        await Program.RunNamedCaseAsync("R33 gallery reads bounded legacy images and keeps production rendering disabled",
            () => WithFixtureAsync(async (project, cache, logger, generator, _) =>
            {
                var legacy = Path.Combine(cache, FilmstripCacheKeyFactory.TryCreateHash(project)!);
                Directory.CreateDirectory(legacy);
                ThumbnailSequenceFrameRenderer.SaveBlankPng(Path.Combine(legacy, "000.png"));
                File.WriteAllText(Path.Combine(legacy, "001.png"), "synthetic not PNG");
                ThumbnailSequenceFrameRenderer.SaveBlankPng(Path.Combine(legacy, "063.png"));
                ThumbnailSequenceFrameRenderer.SaveBlankPng(Path.Combine(legacy, "064.png"));

                var window = new ThumbnailGalleryWindow(project, cache, () => true, logger, generator);
                try
                {
                    await window.ReloadAsync();
                    Ensure(!window.GenerateButton.IsEnabled && window.UnavailableText.Visibility == Visibility.Visible &&
                        window.ReloadButton.IsEnabled && !window.CancelButton.IsEnabled, "production capture was enabled");
                    Ensure(Displayed(window).Select(frame => frame.Number).SequenceEqual([1, 64]) &&
                        Displayed(window).All(frame => frame.Image.IsFrozen), "the numbered legacy images were not displayed");
                    Ensure(window.StatusText.Text.Contains("照合情報なし", StringComparison.Ordinal) &&
                        window.StatusText.Text.Contains("1 点を読み込めません", StringComparison.Ordinal),
                        "legacy evidence or unreadable image was misreported");

                    ThumbnailSequenceFrameRenderer.SaveBlankPng(Path.Combine(legacy, "001.png"));
                    await window.ReloadAsync();
                    Ensure(Displayed(window).Count == 3, "reload did not pick up the repaired legacy image");
                    await window.GenerateAsync();
                    Ensure(!Directory.EnumerateDirectories(cache, "capture-*", SearchOption.AllDirectories).Any(),
                        "production rendering unexpectedly published a capture");
                }
                finally { window.Close(); }
                Ensure(window.FrameList.ItemsSource is null, "closed gallery still retains decoded images");
            }));

        await Program.RunNamedCaseAsync("R33 gallery reports progress and displays 64 frozen slots from an independent source",
            () => WithFixtureAsync(async (project, cache, logger, generator, version) =>
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var source = new Source(version, int.MaxValue);
                source.BeforeCapture = async (frame, token) =>
                {
                    if (frame == 0) return;
                    reached.TrySetResult();
                    await release.Task;
                };
                var window = new ThumbnailGalleryWindow(project, cache, () => true, logger, generator,
                    _ => ValueTask.FromResult<IDetachedThumbnailSource>(source));
                try
                {
                    Ensure(window.GenerateButton.IsEnabled, "the synthetic source was not available to the test window");
                    var pending = window.GenerateAsync();
                    await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Ensure(window.GenerationProgress.Visibility == Visibility.Visible && window.GenerationProgress.Value == 1 &&
                        window.CancelButton.IsEnabled && !window.ReloadButton.IsEnabled, "progress or busy controls were incorrect");
                    release.TrySetResult();
                    await pending.WaitAsync(TimeSpan.FromSeconds(15));
                    var images = Displayed(window);
                    Ensure(images.Count == 64 && images.Select(frame => frame.Number).SequenceEqual(Enumerable.Range(1, 64)) &&
                        images.All(frame => frame.Image.IsFrozen && frame.Image.PixelWidth <= 128 && frame.Image.PixelHeight <= 128),
                        "the gallery did not display the bounded independent thumbnails");
                    Ensure(source.DisposeCount == 1 && source.Frames.Count == 64 && source.Frames[0] == 0 &&
                        source.Frames[^1] == int.MaxValue - 1, "frame-range endpoint overflow or leaked source");
                }
                finally
                {
                    release.TrySetResult();
                    window.Close();
                }
            }));

        await Program.RunNamedCaseAsync("R33 gallery reopens a bound cache and rejects same-size changes and a newer PF version",
            () => WithFixtureAsync(async (project, cache, logger, generator, version) =>
            {
                var result = await generator.GenerateDetachedAsync(project,
                    _ => ValueTask.FromResult<IDetachedThumbnailSource>(new Source(version, 1)), () => true, cache, default);
                Ensure(result.Success, result.Reason);
                var window = new ThumbnailGalleryWindow(project, cache, () => true, logger, generator);
                try
                {
                    await window.ReloadAsync();
                    Ensure(Displayed(window).Count == 64 && !window.StatusText.Text.Contains("旧キャッシュ", StringComparison.Ordinal),
                        "the gallery did not reopen the independent cache with PF evidence");
                    var path = Path.Combine(result.CacheDirectory, "036.png");
                    var original = File.ReadAllBytes(path);
                    var changed = (byte[])original.Clone();
                    changed[^1] ^= 0x01;
                    File.WriteAllBytes(path, changed);
                    await window.ReloadAsync();
                    Ensure(Displayed(window).Count == 0 && window.StatusText.Text.Contains("確認できません", StringComparison.Ordinal) &&
                        File.ReadAllBytes(path).SequenceEqual(changed), "same-size corruption was adopted or deleted");

                    File.WriteAllBytes(path, original);
                    var pf = File.ReadAllBytes(project);
                    var stamp = File.GetLastWriteTimeUtc(project);
                    var substitute = (byte[])pf.Clone();
                    substitute[0] ^= 0x01;
                    try
                    {
                        File.WriteAllBytes(project, substitute);
                        File.SetLastWriteTimeUtc(project, stamp);
                        await window.ReloadAsync();
                        Ensure(Displayed(window).Count == 0 && !window.StatusText.Text.Contains("64 点", StringComparison.Ordinal),
                            "a cached frame was used for a newer PF version");
                    }
                    finally { File.WriteAllBytes(project, pf); }
                }
                finally { window.Close(); }
            }));

        await Program.RunNamedCaseAsync("R33 gallery discards late captures after cancellation, selection change and close",
            () => WithFixtureAsync(async (project, cache, logger, generator, version) =>
            {
                foreach (var end in new[] { "cancel", "selection", "close" })
                {
                    var selected = true;
                    var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var source = new Source(version, 4)
                    {
                        BeforeCapture = async (_, _) =>
                        {
                            reached.TrySetResult();
                            // Deliberately return pixels even after cancellation to emulate a synchronous SDK call.
                            await release.Task;
                        }
                    };
                    var window = new ThumbnailGalleryWindow(project, cache, () => selected, logger, generator,
                        _ => ValueTask.FromResult<IDetachedThumbnailSource>(source));
                    var pending = window.GenerateAsync();
                    try
                    {
                        await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        switch (end)
                        {
                            case "cancel": window.CancelOperation(); break;
                            case "selection": selected = false; break;
                            case "close": window.Close(); break;
                        }
                        release.SetResult();
                        await pending.WaitAsync(TimeSpan.FromSeconds(15));
                        Ensure(Displayed(window).Count == 0 && source.DisposeCount == 1 && source.Frames.Count == 1 &&
                            !Directory.EnumerateFiles(cache, "complete", SearchOption.AllDirectories).Any(),
                            $"late {end} capture was adopted or did not release its resources");
                        if (end == "cancel")
                            Ensure(window.StatusText.Text.Contains("中止", StringComparison.Ordinal) && window.ReloadButton.IsEnabled,
                                "cancellation did not permit retry");
                        if (end == "selection")
                            Ensure(window.StatusText.Text.Contains("変更", StringComparison.Ordinal) && !window.GenerateButton.IsEnabled,
                                "a changed selection still permitted capture");
                    }
                    finally
                    {
                        release.TrySetResult();
                        if (end != "close") window.Close();
                        await pending.WaitAsync(TimeSpan.FromSeconds(15));
                    }
                }
            }));
    }

    private static IReadOnlyList<ThumbnailGalleryFrame> Displayed(ThumbnailGalleryWindow window) =>
        window.FrameList.ItemsSource is IReadOnlyList<ThumbnailGalleryFrame> frames ? frames : [];

    private static async Task WithFixtureAsync(
        Func<string, string, FileLogger, SeekPreviewThumbnailGenerator, ProjectFileIdentity, Task> test)
    {
        var fixture = Directory.CreateTempSubdirectory("r33-thumb-").FullName;
        try
        {
            var project = Path.Combine(fixture, "project.ymmp");
            await File.WriteAllTextAsync(project, "synthetic gallery project");
            var version = await ProjectFileEvidence.ReadAsync(project);
            var cache = Path.Combine(fixture, "cache");
            var logger = new FileLogger(Path.Combine(fixture, "thumb.log"));
            var generator = new SeekPreviewThumbnailGenerator(logger, new CurrentPreviewCaptureService(logger));
            await Program.WpfTestHost.RunAsync(async () =>
            {
                await test(project, cache, logger, generator, version);
                return true;
            });
            Ensure(await ProjectFileEvidence.ReadAsync(project) == version, "the gallery changed the original PF");
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Source(ProjectFileIdentity version, int length) : IDetachedThumbnailSource
    {
        public ProjectFileIdentity ProjectVersion => version;
        public int Length => length;
        internal Func<int, CancellationToken, Task>? BeforeCapture { get; set; }
        internal List<int> Frames { get; } = [];
        internal int DisposeCount { get; private set; }

        public bool IsCurrent() => DisposeCount == 0;

        public async ValueTask<DetachedThumbnailFrame> CaptureAsync(int frame, CancellationToken token)
        {
            if (BeforeCapture is not null) await BeforeCapture(frame, token);
            Frames.Add(frame);
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { (byte)frame, 0, 0, 255 }, 4);
            pixel.Freeze();
            return new(frame, pixel);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
