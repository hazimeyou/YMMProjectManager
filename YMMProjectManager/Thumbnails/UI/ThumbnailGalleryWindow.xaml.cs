using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;

namespace YMMProjectManager.Presentation.Thumbnails;

public partial class ThumbnailGalleryWindow : Window
{
    private readonly string projectPath;
    private readonly string cacheRoot;
    private readonly Func<bool> isSelectionCurrent;
    private readonly FileLogger logger;
    private readonly SeekPreviewThumbnailGenerator generator;
    private readonly Func<CancellationToken, ValueTask<IDetachedThumbnailSource>>? openSource;
    private CancellationTokenSource? active;
    private bool closed;

    internal ThumbnailGalleryWindow(string projectPath, string cacheRoot, Func<bool> isSelectionCurrent,
        FileLogger logger, SeekPreviewThumbnailGenerator generator,
        Func<CancellationToken, ValueTask<IDetachedThumbnailSource>>? openSource = null)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.cacheRoot = cacheRoot;
        this.isSelectionCurrent = isSelectionCurrent;
        this.logger = logger;
        this.generator = generator;
        this.openSource = openSource;
        ProjectNameText.Text = Path.GetFileName(projectPath);
        UnavailableText.Visibility = openSource is null ? Visibility.Visible : Visibility.Collapsed;
        GenerationProgress.Maximum = ThumbnailCachePublication.SlotCount;
        UpdateButtons();
        Loaded += OnLoaded;
    }

    internal Task ReloadAsync() => RunAsync(generate: false);
    internal Task GenerateAsync() => openSource is null ? Task.CompletedTask : RunAsync(generate: true);

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        await ReloadAsync();
    }

    private async void OnReloadClick(object sender, RoutedEventArgs args) => await ReloadAsync();
    private async void OnGenerateClick(object sender, RoutedEventArgs args) => await GenerateAsync();
    private void OnCancelClick(object sender, RoutedEventArgs args) => CancelOperation();

    internal void CancelOperation()
    {
        active?.Cancel();
        UpdateButtons();
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        active?.Cancel();
        FrameList.ItemsSource = null;
        base.OnClosed(e);
    }

    private async Task RunAsync(bool generate)
    {
        if (closed || active is not null) return;
        FrameList.ItemsSource = null;
        if (!isSelectionCurrent())
        {
            StatusText.Text = "一覧で選択していたプロジェクトが変更されました。";
            UpdateButtons();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        active = cancellation;
        var token = cancellation.Token;
        GenerationProgress.Visibility = generate ? Visibility.Visible : Visibility.Collapsed;
        GenerationProgress.Value = 0;
        StatusText.Text = generate ? "サムネイルを取得しています。" : "キャッシュを確認しています。";
        UpdateButtons();
        try
        {
            if (generate)
            {
                var result = await generator.GenerateDetachedAsync(projectPath, openSource!, isSelectionCurrent,
                    cacheRoot, token, completed => ReportProgressAsync(completed, cancellation));
                token.ThrowIfCancellationRequested();
                if (closed) return;
                if (!isSelectionCurrent())
                {
                    StatusText.Text = "一覧で選択していたプロジェクトが変更されました。";
                    return;
                }
                if (!result.Success)
                {
                    StatusText.Text = result.Reason;
                    return;
                }
            }
            await Task.Run(() => ThumbnailGalleryCache.ReadAsync(cacheRoot, projectPath, token, ShowFramesAsync), token);

            Task ShowFramesAsync(IReadOnlyList<ThumbnailGalleryFrame> frames, string status)
            {
                return Dispatcher.InvokeAsync(() =>
                {
                    if (closed || !ReferenceEquals(active, cancellation)) return;
                    token.ThrowIfCancellationRequested();
                    if (!isSelectionCurrent())
                    {
                        StatusText.Text = "一覧で選択していたプロジェクトが変更されました。";
                        return;
                    }
                    FrameList.ItemsSource = frames;
                    StatusText.Text = status;
                }, DispatcherPriority.Normal, token).Task;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!closed) StatusText.Text = "中止しました。必要に応じてキャッシュを再読込してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or NotSupportedException or
                                  JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            logger.Error("Thumbnail gallery", ex);
            if (!closed) StatusText.Text = "対象プロジェクトまたはキャッシュを確認できません。キャッシュは保持されています。";
        }
        finally
        {
            active = null;
            if (!closed) UpdateButtons();
        }
    }

    private async ValueTask ReportProgressAsync(int completed, CancellationTokenSource operation)
    {
        if (!closed && ReferenceEquals(active, operation) && !operation.IsCancellationRequested && isSelectionCurrent())
        {
            GenerationProgress.Value = completed;
            StatusText.Text = $"サムネイルを取得しています（{completed} / {ThumbnailCachePublication.SlotCount}）。";
        }
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    private void UpdateButtons()
    {
        var idle = !closed && active is null && isSelectionCurrent();
        ReloadButton.IsEnabled = idle;
        GenerateButton.IsEnabled = idle && openSource is not null;
        CancelButton.IsEnabled = active is { IsCancellationRequested: false };
    }
}
