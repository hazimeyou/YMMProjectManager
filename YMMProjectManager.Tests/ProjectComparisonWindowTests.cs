using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMMProjectManager.Application.PreviewComparison;
using YMMProjectManager.Presentation.Views;

internal static class ProjectComparisonWindowTests
{
    private static readonly Guid ConfigurationId = Guid.NewGuid();
    private static readonly PreviewSource Original = new(Guid.NewGuid(), ConfigurationId, new(24, 1), 48);
    private static readonly PreviewSource Current = new(Guid.NewGuid(), ConfigurationId, new(30, 1), 30);

    internal static async Task RunAsync()
    {
        await Program.RunNamedCaseAsync("R33 disconnected comparison window edits times and offset sections", EditTimingAsync);
        await Program.RunNamedCaseAsync("R33 comparison timing UI loads safely and saves only new JSON", PersistTimingAsync);
        await Program.RunNamedCaseAsync("R33 comparison window displays synthetic frames at rational rates and reports range", RenderPairAsync);
        await Program.RunNamedCaseAsync("R33 comparison window clears stale images and discards cancelled captures", CancelCaptureAsync);
    }

    private static Task EditTimingAsync() => WithWindowAsync(null, (window, workspace, _, _) =>
    {
        Require(Control<Slider>(window, "CommonSeekBar") is { Maximum: 60d }, "Offline common seek bar is missing.");
        Require(Control<TextBlock>(window, "DisconnectedNotice").Visibility == Visibility.Visible &&
            !Control<Button>(window, "RefreshPreviewButton").IsEnabled, "Product window exposed an unverified renderer.");
        Require(workspace.EffectiveOffset == TimeSpan.Zero && workspace.CurrentTimeLabel.StartsWith("00:00:00"),
            "Default zero offset is incorrect.");
        SetText(window, "FixedOffsetInput", "-00:00:00.25");
        Click(window, "ApplyFixedOffsetButton");
        Require(workspace.CurrentTimeLabel == "範囲外", "Negative current time was not indicated.");
        SetText(window, "FixedOffsetInput", "+-00:00:01");
        Click(window, "ApplyFixedOffsetButton");
        Require(Error(window).Contains("オフセット") && workspace.DefaultOffset == TimeSpan.FromMilliseconds(-250),
            "Invalid signed time changed the committed setting.");

        AddSection(window, "00:00:01", "+00:00:02");
        AddSection(window, "00:00:03", "-00:00:00.5");
        AddSection(window, "00:00:01", "+00:00:04");
        Require(Error(window).Contains("重複") && workspace.Sections.Count == 2, "Duplicate interval modified timing.");
        AddSection(window, "-00:00:01", "00:00:00");
        Require(Error(window).Contains("時刻") && workspace.Sections.Count == 2, "Negative interval modified timing.");

        Control<Slider>(window, "CommonSeekBar").Value = 1d;
        Require(workspace.CurrentTimeLabel.StartsWith("00:00:03"), "First inclusive section boundary was lost.");
        Control<Slider>(window, "CommonSeekBar").Value = 3d;
        Require(workspace.CurrentTimeLabel.StartsWith("00:00:02.5000000"), "Second section boundary was lost.");

        var list = Control<DataGrid>(window, "SectionList");
        list.SelectedIndex = 0;
        list.SelectedIndex = 1;
        SetText(window, "SectionOffsetInput", "+00:00:05");
        Click(window, "EditSectionButton");
        Require(workspace.CurrentTimeLabel.StartsWith("00:00:08"), "Editing an interval did not update planned times.");
        Click(window, "RemoveSectionButton");
        Require(workspace.Sections.Count == 1 && workspace.CurrentTimeLabel.StartsWith("00:00:05"),
            "Deleting the last section did not restore the previous interval.");
        SetText(window, "CommonTimeInput", "00:02:10");
        Click(window, "ApplyTimeButton");
        Require(Control<Slider>(window, "CommonSeekBar").Value == 130 &&
            Control<Slider>(window, "CommonSeekBar").Maximum >= 130, "Typed time did not extend the offline seek range.");
        return Task.CompletedTask;
    });

    private static Task PersistTimingAsync() => WithWindowAsync(null, async (window, workspace, root, original) =>
    {
        SetText(window, "FixedOffsetInput", "-00:00:00.5");
        Click(window, "ApplyFixedOffsetButton");
        AddSection(window, "00:01:00", "+00:00:02");
        var originalBytes = File.ReadAllBytes(original);
        await window.SaveTimingAsync(original);
        Require(Error(window).Contains("保存先") && originalBytes.SequenceEqual(File.ReadAllBytes(original)),
            "Saving timing was allowed to modify the selected PF.");

        var path = Path.Combine(root, "timing.json");
        await window.SaveTimingAsync(path);
        var saved = File.ReadAllBytes(path);
        workspace.SetDefaultOffset(TimeSpan.FromSeconds(12));
        await window.SaveTimingAsync(path);
        Require(Error(window).Contains("同名") && saved.SequenceEqual(File.ReadAllBytes(path)),
            "A duplicate timing path overwrote an earlier copy.");
        await window.LoadTimingAsync(path);
        Require(workspace.DefaultOffset == TimeSpan.FromMilliseconds(-500) &&
            workspace.Sections is [{ Start.TotalSeconds: 60d, Offset.TotalSeconds: 2d }] &&
            Control<TextBox>(window, "FixedOffsetInput").Text == "-00:00:00.5000000",
            "Loading copied timing did not update the workspace and editor.");

        var broken = Path.Combine(root, "invalid.json");
        await File.WriteAllTextAsync(broken, """{"Version":1,"DefaultOffsetTicks":0,"Sections":[{"StartTicks":1,"OffsetTicks":0},{"StartTicks":1,"OffsetTicks":4}]}""");
        await window.LoadTimingAsync(broken);
        Require(Error(window).Contains("ファイル形式") && workspace.Sections.Count == 1 &&
            workspace.DefaultOffset == TimeSpan.FromMilliseconds(-500), "A malformed file replaced accepted timing.");
    });

    private static Task RenderPairAsync()
    {
        var synthetic = new SyntheticPreviewProvider();
        return WithWindowAsync(synthetic, async (window, workspace, _, _) =>
        {
            Require(Control<TextBlock>(window, "DisconnectedNotice").Visibility == Visibility.Collapsed &&
                Control<Button>(window, "RefreshPreviewButton").IsEnabled, "Synthetic UI did not connect its supplied provider.");
            workspace.SetSection(null, new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(200)));
            Control<Slider>(window, "CommonSeekBar").Value = 0.5;
            Click(window, "RefreshPreviewButton");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(synthetic.LastAlignment is { FrameA: 12, FrameB: 21 } &&
                Control<Image>(window, "OriginalPreview").Source is not null &&
                Control<Image>(window, "CurrentPreview").Source is not null &&
                workspace.CurrentTimeLabel.Contains("フレーム 21"), "Synthetic images or FPS-aligned times were not shown.");

            Control<Slider>(window, "CommonSeekBar").Value = 0.8;
            Click(window, "RefreshPreviewButton");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(synthetic.RenderCount == 1 && workspace.CurrentTimeLabel.Contains("範囲外") &&
                workspace.OriginalTimeLabel.Contains("フレーム 19") && workspace.OriginalImage is null &&
                Control<Image>(window, "CurrentPreview").Source is null, "A prior image survived an out-of-range seek.");

            synthetic.Failure = new IOException("private provider detail");
            Control<Slider>(window, "CommonSeekBar").Value = 0.5;
            Click(window, "RefreshPreviewButton");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(workspace.PreviewStatus.Contains("取得できませんでした") &&
                !workspace.PreviewStatus.Contains("private provider detail") && workspace.OriginalImage is null,
                "Renderer failure was not displayed safely.");
        });
    }

    private static Task CancelCaptureAsync()
    {
        var pending = new TaskCompletionSource<ComparisonPreviewImages>(TaskCreationOptions.RunContinuationsAsynchronously);
        var synthetic = new SyntheticPreviewProvider { Pending = pending };
        return WithWindowAsync(synthetic, async (window, workspace, _, _) =>
        {
            var old = workspace.RefreshPreviewAsync();
            Require(Control<Button>(window, "CancelPreviewButton").IsEnabled, "Cancel operation is unavailable.");
            Click(window, "CancelPreviewButton");
            Control<Slider>(window, "CommonSeekBar").Value = 0.25;
            Require(synthetic.RequestToken.IsCancellationRequested && !workspace.CanRender,
                "Provider was not cancelled or could overlap a pending capture.");
            pending.SetResult(SyntheticPreviewProvider.NewImages());
            await old;
            Require(workspace.OriginalImage is null && workspace.CurrentImage is null && workspace.CanRender,
                "A cancelled stale capture reached the preview.");
            synthetic.Pending = null;
            await workspace.RefreshPreviewAsync();
            Require(synthetic.LastAlignment is { FrameA: 6, FrameB: 7 } && workspace.CurrentImage is not null,
                "The workspace could not retry at the new time.");
        });
    }

    private static Task WithWindowAsync(SyntheticPreviewProvider? provider,
        Func<ProjectComparisonWindow, ProjectComparisonWorkspace, string, string, Task> run) =>
        Program.WpfTestHost.RunAsync(async () =>
        {
            var root = Directory.CreateTempSubdirectory("pmcompare");
            ProjectComparisonWindow? window = null;
            try
            {
                var a = Path.Combine(root.FullName, "original.ymmp");
                var b = Path.Combine(root.FullName, "current.ymmp");
                await File.WriteAllTextAsync(a, """{"Name":"original"}""");
                await File.WriteAllTextAsync(b, """{"Name":"current"}""");
                var workspace = new ProjectComparisonWorkspace(provider);
                workspace.SetProject(PreviewComparisonSide.A, a);
                workspace.SetProject(PreviewComparisonSide.B, b);
                window = new ProjectComparisonWindow(workspace);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await run(window, workspace, root.FullName, a);
                Require(await File.ReadAllTextAsync(a) == """{"Name":"original"}""" &&
                    await File.ReadAllTextAsync(b) == """{"Name":"current"}""", "Comparison modified the PFs.");
                return true;
            }
            finally
            {
                window?.Close();
                root.Delete(recursive: true);
            }
        });

    private static void AddSection(ProjectComparisonWindow window, string start, string offset)
    {
        SetText(window, "SectionStartInput", start);
        SetText(window, "SectionOffsetInput", offset);
        Click(window, "AddSectionButton");
    }

    private static T Control<T>(ProjectComparisonWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Comparison control missing: {name}");

    private static string Error(ProjectComparisonWindow window) => Control<TextBlock>(window, "EditStatus").Text;

    private static void SetText(ProjectComparisonWindow window, string name, string text) =>
        Control<TextBox>(window, name).Text = text;

    private static void Click(ProjectComparisonWindow window, string name) =>
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    private sealed class SyntheticPreviewProvider : IProjectComparisonPreviewProvider
    {
        internal PreviewFrameAlignment? LastAlignment { get; private set; }
        internal int RenderCount { get; private set; }
        internal CancellationToken RequestToken { get; private set; }
        internal TaskCompletionSource<ComparisonPreviewImages>? Pending { get; set; }
        internal IOException? Failure { get; set; }

        public ValueTask<ComparisonPreviewSources> ResolveAsync(string original, string current, CancellationToken token)
        {
            Require(original != current, "Comparison did not pass distinct PF paths.");
            return ValueTask.FromResult(new ComparisonPreviewSources(Original, Current));
        }

        public ValueTask<ComparisonPreviewImages> RenderAsync(ComparisonPreviewSources sources,
            PreviewFrameAlignment alignment, CancellationToken token)
        {
            RenderCount++;
            RequestToken = token;
            LastAlignment = alignment;
            if (Failure is not null) throw Failure;
            return Pending is { } delayed ? new(delayed.Task) : ValueTask.FromResult(NewImages());
        }

        internal static ComparisonPreviewImages NewImages() => new(FrozenPixel(10), FrozenPixel(20));

        private static BitmapSource FrozenPixel(byte red)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, red, 255 }, 4);
            image.Freeze();
            return image;
        }
    }
}
