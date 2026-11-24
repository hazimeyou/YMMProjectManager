using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Application.PreviewComparison;

internal readonly record struct ComparisonPreviewSources(PreviewSource Original, PreviewSource Current);
internal readonly record struct ComparisonPreviewImages(BitmapSource? Original, BitmapSource? Current);

/// <summary>The adapter owns source validation, pixel copies and host resources. Images must be frozen.</summary>
internal interface IProjectComparisonPreviewProvider
{
    ValueTask<ComparisonPreviewSources> ResolveAsync(string original, string current, CancellationToken token);
    ValueTask<ComparisonPreviewImages> RenderAsync(ComparisonPreviewSources sources, PreviewFrameAlignment alignment,
        CancellationToken token);
}

internal sealed class ProjectComparisonWorkspace : INotifyPropertyChanged, IDisposable
{
    private readonly IProjectComparisonPreviewProvider? previewProvider;
    private ComparisonPreviewSources? sources;
    private CancellationTokenSource? renderCancellation;
    private ComparisonTiming timing = new(TimeSpan.Zero, []);
    private bool disposed;

    public ProjectComparisonWorkspace(IProjectComparisonPreviewProvider? previewProvider = null) =>
        this.previewProvider = previewProvider;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? OriginalPath { get; private set; }
    public string? CurrentPath { get; private set; }
    public TimeSpan CommonTime { get; private set; }
    public TimeSpan EffectiveOffset => timing.GetOffset(CommonTime);
    public TimeSpan DefaultOffset => timing.DefaultOffset;
    internal double CommonSeconds
    {
        get => CommonTime.TotalSeconds;
        set => SetCommonTime(value >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(value));
    }
    internal double SeekMaximumSeconds
    {
        get
        {
            var duration = sources is { } known
                ? Math.Max(Seconds(known.Original), Seconds(known.Current)) : 60d;
            return Math.Max(Math.Max(duration, CommonSeconds), 1d);
        }
    }
    public bool IsRendererConnected => previewProvider is not null;
    public bool CanRender => !disposed && IsRendererConnected && renderCancellation is null &&
        OriginalPath is not null && CurrentPath is not null &&
        !string.Equals(OriginalPath, CurrentPath, StringComparison.OrdinalIgnoreCase);
    public bool CanCancel => !disposed && renderCancellation is { IsCancellationRequested: false };
    public string PreviewStatus { get; private set; } = "";
    public BitmapSource? OriginalImage { get; private set; }
    public BitmapSource? CurrentImage { get; private set; }
    public IReadOnlyList<ComparisonOffset> Sections => timing.Sections;
    public string CommonTimeLabel => FormatTime(CommonTime);
    public string OriginalTimeLabel => DisplayTime(original: true);
    public string CurrentTimeLabel => DisplayTime(original: false);
    public string EffectiveOffsetLabel => FormatTime(EffectiveOffset, signed: true);

    internal void SetProject(PreviewComparisonSide side, string path)
    {
        var fullPath = ComparisonFilePaths.GetLocalFile(path);
        if (!fullPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase))
            throw new IOException("存在するPF（.ymmp）を選択してください。");
        using var directory = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(fullPath)!, enforceShare: true);
        using var project = OwnedFilePublication.OpenOrdinaryPath(fullPath, expectDirectory: false, allowRename: false);
        if (side == PreviewComparisonSide.A)
            OriginalPath = fullPath;
        else
            CurrentPath = fullPath;
        sources = null;
        InvalidatePreview();
    }

    internal void SetCommonTime(TimeSpan time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        if (CommonTime == time) return;
        CommonTime = time;
        InvalidatePreview();
    }

    internal void SetDefaultOffset(TimeSpan offset) => SetTiming(new ComparisonTiming(offset, Sections));

    internal void SetSection(ComparisonOffset? previous, ComparisonOffset section)
    {
        var intervals = Sections.ToList();
        if (previous is { } original && !intervals.Remove(original))
            throw new InvalidOperationException("区間を選び直してください。");
        intervals.Add(section);
        SetTiming(new ComparisonTiming(DefaultOffset, intervals.OrderBy(x => x.Start).ToArray()));
    }

    internal void RemoveSection(ComparisonOffset section) =>
        SetTiming(new ComparisonTiming(DefaultOffset, Sections.Where(candidate => candidate != section).ToArray()));

    internal Task SaveTimingCopyAsync(string path, CancellationToken token)
    {
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new IOException("比較設定の保存名には .json を指定してください。");
        return timing.SaveCopyAsync(path, token);
    }

    internal async Task LoadTimingAsync(string path, CancellationToken token)
    {
        var previous = timing;
        var loaded = await ComparisonTiming.LoadAsync(path, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(previous, timing))
            throw new IOException("読み込み中に比較設定が編集されました。もう一度読み込んでください。");
        SetTiming(loaded);
    }

    internal async Task RefreshPreviewAsync()
    {
        if (!CanRender || previewProvider is null) return;

        using var cancellation = new CancellationTokenSource();
        renderCancellation = cancellation;
        var token = cancellation.Token;
        OriginalImage = CurrentImage = null;
        PreviewStatus = "プレビューを取得しています。";
        RefreshProperties();
        try
        {
            var observed = await previewProvider.ResolveAsync(OriginalPath!, CurrentPath!, token);
            token.ThrowIfCancellationRequested();
            var alignment = PreviewFrameAlignment.Align(timing.Bind(CommonTime, observed.Original, observed.Current));
            sources = alignment.Error is PreviewComparisonError.None or PreviewComparisonError.OutsideCommonDuration ? observed : null;
            if (alignment.Error != PreviewComparisonError.None)
            {
                PreviewStatus = alignment.Error == PreviewComparisonError.OutsideCommonDuration
                    ? "選択した時刻では両方のフレームを取得できません。"
                    : "比較する描画元を照合できませんでした。";
                return;
            }
            var captured = await previewProvider.RenderAsync(observed, alignment, token);
            token.ThrowIfCancellationRequested();
            if (captured.Original is not { IsFrozen: true } || captured.Current is not { IsFrozen: true })
                throw new InvalidOperationException("比較画像を受け取れませんでした。");
            (OriginalImage, CurrentImage) = captured;
            PreviewStatus = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or TimeoutException or OperationCanceledException or AggregateException)
        {
            if (!token.IsCancellationRequested)
                PreviewStatus = "プレビューを取得できませんでした。対象と時刻を確認してください。";
        }
        finally
        {
            renderCancellation = null;
            RefreshProperties();
        }
    }

    internal void CancelPreview() => InvalidatePreview("プレビューの取得を中止しました。");

    public void Dispose()
    {
        disposed = true;
        InvalidatePreview();
    }

    private void SetTiming(ComparisonTiming updated)
    {
        timing = updated;
        InvalidatePreview();
    }

    private void InvalidatePreview(string status = "")
    {
        try { renderCancellation?.Cancel(); }
        catch (AggregateException)
        {
            status = "描画元を正常に中止できませんでした。";
        }
        OriginalImage = CurrentImage = null;
        PreviewStatus = status;
        RefreshProperties();
    }

    private string DisplayTime(bool original)
    {
        var requested = original ? CommonTime : timing.RequestedCurrentTime(CommonTime);
        if (requested is null || requested < TimeSpan.Zero) return "範囲外";
        var label = FormatTime(requested.Value);
        if (sources is not { } observed) return label + "（表示予定）";
        var alignment = PreviewFrameAlignment.Align(timing.Bind(CommonTime, observed.Original, observed.Current));
        if (alignment.Error is not (PreviewComparisonError.None or PreviewComparisonError.OutsideCommonDuration))
            return label + "（未確認）";
        if ((original ? alignment.FrameA : alignment.FrameB) is not { } frame)
            return $"{label}（範囲外）";
        var rate = (original ? observed.Original : observed.Current).FrameRate;
        var start = (Int128)frame * TimeSpan.TicksPerSecond * rate.Denominator / rate.Numerator;
        var display = (original ? OriginalImage : CurrentImage) is null ? "・未表示" : "";
        return $"{FormatTime(TimeSpan.FromTicks((long)start))}（フレーム {frame}{display}）";
    }

    private static double Seconds(PreviewSource source) => source.FrameRate is { Numerator: > 0, Denominator: > 0 }
        ? (double)source.FrameCount * source.FrameRate.Denominator / source.FrameRate.Numerator : 0d;

    private void RefreshProperties() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    internal static bool TryReadTime(string value, out TimeSpan time)
    {
        value = value.Trim();
        if (value.StartsWith('+'))
        {
            value = value[1..];
            if (value.StartsWith('-'))
            {
                time = default;
                return false;
            }
        }
        time = default;
        return value.Count(character => character == ':') == 2 &&
            TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out time);
    }

    internal static string FormatTime(TimeSpan time, bool signed = false) =>
        (signed && time > TimeSpan.Zero ? "+" : "") + time.ToString("c", CultureInfo.InvariantCulture);
}
