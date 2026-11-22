using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;

namespace YMMProjectManager.Presentation.Controls;

/// <summary>
/// マウス位置に応じて 64 枚のサムネイルを切り替える一覧用プレビューコントロールです。
/// </summary>
//CSJ,class=ScrubThumbnailControl,位置移動用サムネイルコントロール
public partial class ScrubThumbnailControl : UserControl
{
    private const int ThumbCount = 64;
    //CSJ,field=Logger,ロガー
    private static readonly FileLogger Logger = PluginDiagnostics.Logger;

    //CSJ,field=currentIndex,現在のインデックス
    private int currentIndex = -1;
    //CSJ,field=loadVersion,読み込みバージョン
    private int loadVersion;

    //CSJ,field=CacheDirectoryProperty,キャッシュディレクトリプロパティ
    public static readonly DependencyProperty CacheDirectoryProperty =
        DependencyProperty.Register(
            nameof(CacheDirectory),
            typeof(string),
            typeof(ScrubThumbnailControl),
            new PropertyMetadata(null, OnCacheDirectoryChanged));

    //CSJ,property=CacheDirectory,キャッシュディレクトリ
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

    //CSJ,method=OnLoaded,読み込み済み時の処理
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await SetIndexAsync(0);
    }

    //CSJ,method=OnMouseMove,マウス移動時の処理
    private async void OnMouseMove(object sender, MouseEventArgs e)
    {
        //CSJ,variable=width,幅
        var width = ActualWidth <= 0 ? 64d : ActualWidth;
        //CSJ,variable=point,地点
        var point = e.GetPosition(this);
        // 横位置を 0..63 のスロット番号へ変換し、タイムラインをなぞる感覚で表示する。
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

    //CSJ,method=SetIndexAsync,インデックスを設定する（非同期）
    private async Task SetIndexAsync(int index)
    {
        currentIndex = index;
        // 古い非同期ロード完了が後から UI を上書きしないよう、世代番号で破棄する。
        //CSJ,variable=version,バージョン
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

            //CSJ,variable=indexPath,インデックスパス
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

            // 指定スロットが未生成でも、先頭画像があれば最低限のプレビューを表示する。
            //CSJ,variable=fallback,代替
            var fallback = Path.Combine(CacheDirectory, "000.png");
            return await ThumbnailImageLoader.LoadAsync(fallback, Logger).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.Error("Scrub thumbnail load failed", ex);
            return null;
        }
    }

    //CSJ,method=OnCacheDirectoryChanged,キャッシュディレクトリ変更済み時の処理
    private static void OnCacheDirectoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrubThumbnailControl control)
        {
            return;
        }

        control.currentIndex = -1;
        _ = control.SetIndexAsync(0);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}

