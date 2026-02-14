using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;

namespace YMMProjectManager.Presentation.Controls;

public partial class ScrubThumbnailControl : UserControl
{
    private const int ThumbCount = 64;
    private static readonly FileLogger Logger = CreateLogger();

    private int currentIndex = -1;
    private int loadVersion;

    public static readonly DependencyProperty CacheDirectoryProperty =
        DependencyProperty.Register(
            nameof(CacheDirectory),
            typeof(string),
            typeof(ScrubThumbnailControl),
            new PropertyMetadata(null, OnCacheDirectoryChanged));

    public string? CacheDirectory
    {
        get => (string?)GetValue(CacheDirectoryProperty);
        set => SetValue(CacheDirectoryProperty, value);
    }

    public ScrubThumbnailControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await SetIndexAsync(0);
    }

    private async void OnMouseMove(object sender, MouseEventArgs e)
    {
        var width = ActualWidth <= 0 ? 64d : ActualWidth;
        var point = e.GetPosition(this);
        var ratio = point.X / width;
        var idx = Clamp((int)Math.Floor(ratio * ThumbCount), 0, ThumbCount - 1);
        if (idx == currentIndex)
        {
            return;
        }

        await SetIndexAsync(idx);
    }

    private async void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (currentIndex == 0)
        {
            return;
        }

        await SetIndexAsync(0);
    }

    private async Task SetIndexAsync(int index)
    {
        currentIndex = index;
        var version = ++loadVersion;
        var source = await LoadBestSourceAsync(index);
        if (version != loadVersion)
        {
            return;
        }

        ThumbnailImage.Source = source;
        if (source is null)
        {
            ThumbnailImage.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
        }
        else
        {
            ThumbnailImage.Visibility = Visibility.Visible;
            Placeholder.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<ImageSource?> LoadBestSourceAsync(int index)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CacheDirectory))
            {
                return null;
            }

            var indexPath = Path.Combine(CacheDirectory, $"{index:000}.png");
            var source = await ThumbnailImageLoader.LoadAsync(indexPath, Logger).ConfigureAwait(true);
            if (source is not null)
            {
                return source;
            }

            if (index == 0)
            {
                return null;
            }

            var fallback = Path.Combine(CacheDirectory, "000.png");
            return await ThumbnailImageLoader.LoadAsync(fallback, Logger).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.Error("Scrub thumbnail load failed", ex);
            return null;
        }
    }

    private static void OnCacheDirectoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrubThumbnailControl control)
        {
            return;
        }

        control.currentIndex = -1;
        _ = control.SetIndexAsync(0);
    }

    private static FileLogger CreateLogger()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ScrubThumbnailControl).Assembly.Location) ?? AppContext.BaseDirectory;
        var logPath = Path.Combine(assemblyDir, "logs", "YMMProjectManager.log");
        return new FileLogger(logPath);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}

