using YMMProjectManager.Application.PreviewComparison;

//CSJ,class=PreviewComparisonTests,プレビュー比較テスト
internal static class PreviewComparisonTests
{
    //CSJ,field=Configuration,構成
    private static readonly Guid Configuration = Guid.Parse("00000000-0000-0000-0000-000000000010");
    private static readonly PreviewSource A = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), Configuration, new(24, 1), 48);
    private static readonly PreviewSource B = new(
        Guid.Parse("00000000-0000-0000-0000-000000000002"), Configuration, new(30, 1), 30);

    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync()
    {
        await CaseAsync("TL-1 same rate, mixed rate and half-open frame boundaries", TestAlignment);
        await CaseAsync("TL-1 invalid sources, empty scenes and exact rational rate", TestInvalidAlignment);
        await CaseAsync("TL-1 matching buffers, single pixel and alpha differences", TestMetrics);
        await CaseAsync("TL-1 invalid frame, size, length and format classification", TestInvalidFrames);
        await CaseAsync("TL-1 cancellation and stateless retry", TestCancellation);
        await CaseAsync("TL-1 cancellation during buffer acquisition", TestCancellationDuringBufferAcquisition);
        await CaseAsync("TL-1 4K metrics, wide accumulator and bounded allocation", TestLargeImage);
        await Program.RunNamedCaseAsync("TL-1 concurrent comparisons with stable independent buffers", TestConcurrencyAsync);
    }

    private static Task CaseAsync(string name, Action run) => Program.RunNamedCaseAsync(name, () =>
    {
        run();
        return Task.CompletedTask;
    });

    //CSJ,method=TestAlignment,整合をテストする
    private static void TestAlignment()
    {
        var sameRate = B with { FrameRate = new(48, 2), FrameCount = 24 };
        ExpectAlignment(new(TimeSpan.Zero, A, sameRate), 0, 0);
        ExpectAlignment(new(TimeSpan.FromTicks(4_999_999), A, sameRate), 11, 11);
        ExpectAlignment(new(TimeSpan.FromTicks(5_000_000), A, sameRate), 12, 12);
        ExpectAlignment(new(TimeSpan.FromTicks(333_333), A, B), 0, 0);
        ExpectAlignment(new(TimeSpan.FromTicks(333_334), A, B), 0, 1);
        ExpectAlignment(new(TimeSpan.FromTicks(9_999_999), A, B), 23, 29);
        ExpectAlignment(new(TimeSpan.FromSeconds(1), A, B), 24, null,
            PreviewComparisonError.OutsideCommonDuration, PreviewComparisonSide.B);
        ExpectAlignment(new(TimeSpan.FromSeconds(1), B, A), null, 24,
            PreviewComparisonError.OutsideCommonDuration, PreviewComparisonSide.A);
        ExpectAlignment(new(TimeSpan.FromSeconds(2), A, B), null, null,
            PreviewComparisonError.OutsideCommonDuration);

        var fewerFrames = B with { FrameCount = 24 };
        ExpectAlignment(new(TimeSpan.FromMilliseconds(800), A, fewerFrames), 19, null,
            PreviewComparisonError.OutsideCommonDuration, PreviewComparisonSide.B);
        var equalDuration = B with { FrameCount = 60 };
        ExpectAlignment(new(TimeSpan.FromTicks(19_999_999), A, equalDuration), 47, 59);
        ExpectAlignment(new(TimeSpan.FromSeconds(2), A, equalDuration), null, null,
            PreviewComparisonError.OutsideCommonDuration);
    }

    //CSJ,method=TestInvalidAlignment,無効整合をテストする
    private static void TestInvalidAlignment()
    {
        ExpectAlignment(new(TimeSpan.FromTicks(-1), A, B), null, null, PreviewComparisonError.NegativeTimestamp);
        ExpectAlignment(new(TimeSpan.Zero, default, B), null, null,
            PreviewComparisonError.InvalidSource, PreviewComparisonSide.A);
        //CSJ,variable=invalid,無効
        foreach (var invalid in new[] { B with { FrameRate = new(0, 1) }, B with { FrameRate = new(24, -1) },
            B with { FrameCount = -1 }, B with { RenderingConfigurationId = Guid.Empty } })
        {
            ExpectAlignment(new(TimeSpan.Zero, A, invalid), null, null,
                PreviewComparisonError.InvalidSource, PreviewComparisonSide.B);
        }

        ExpectAlignment(new(TimeSpan.Zero, A, A), null, null, PreviewComparisonError.SameSnapshot);
        ExpectAlignment(new(TimeSpan.Zero, A, B with { RenderingConfigurationId = B.SnapshotId }),
            null, null, PreviewComparisonError.RenderingConfigurationMismatch);
        ExpectAlignment(new(TimeSpan.Zero, A with { FrameCount = 0 }, B), null, 0,
            PreviewComparisonError.OutsideCommonDuration, PreviewComparisonSide.A);

        var ntsc = A with { FrameRate = new(30_000, 1_001), FrameCount = 60_000 };
        var thirty = B with { FrameCount = 60_000 };
        ExpectAlignment(new(TimeSpan.FromTicks(10_009_999), ntsc, thirty), 29, 30);
        ExpectAlignment(new(TimeSpan.FromTicks(10_010_000), ntsc, thirty), 30, 30);

        var extreme = B with { FrameRate = new(int.MaxValue, int.MaxValue), FrameCount = int.MaxValue };
        ExpectAlignment(new(TimeSpan.FromSeconds(2_147_483_646), A, extreme), null, 2_147_483_646,
            PreviewComparisonError.OutsideCommonDuration, PreviewComparisonSide.A);
        ExpectAlignment(new(TimeSpan.MaxValue, A, extreme), null, null,
            PreviewComparisonError.OutsideCommonDuration);
    }

    //CSJ,method=TestMetrics,測定値をテストする
    private static void TestMetrics()
    {
        byte[] original = [10, 60, 0, 100, 20, 30, 40, 50];
        var a = Frame(A, original);
        var identical = Compare(a, Frame(B, (byte[])original.Clone()));
        Ensure(identical.ExactlyEqual && identical.ChangedPixels == 0 && identical.ChangedPixelPercentage == 0 &&
            identical.MeanAbsoluteError == 0 && identical.MaximumChannelDifference == 0, "Identical buffers");

        //CSJ,variable=changed,変更済み
        var changed = Frame(B, [110, 40, 10, 110, 20, 30, 40, 50]);
        //CSJ,variable=metrics,測定値
        var metrics = Compare(a, changed);
        Ensure(metrics == new PreviewImageMetrics(2, 1, 1, 100, 140) && !metrics.ExactlyEqual &&
            metrics.MeanAbsoluteError == 17.5 && metrics.ChangedPixelPercentage == 50, "Four-channel metrics");
        Ensure(Compare(Frame(A, changed.Pixels.ToArray()), Frame(B, original)) == metrics, "Absolute differences");

        //CSJ,variable=alpha,アルファ
        var alpha = Compare(Frame(A, [8, 9, 10, 0], 1), Frame(B, [8, 9, 10, 40], 1));
        Ensure(alpha == new PreviewImageMetrics(1, 1, 1, 40, 40) && alpha.MeanAbsoluteError == 10,
            "Alpha-only change");
        var hiddenRgb = Compare(Frame(A, [10, 0, 0, 0], 1), Frame(B, [0, 0, 0, 0], 1));
        Ensure(hiddenRgb == new PreviewImageMetrics(1, 1, 1, 10, 10), "RGB at alpha zero");
    }

    //CSJ,method=TestInvalidFrames,無効フレーム列をテストする
    private static void TestInvalidFrames()
    {
        var a = Frame(A, new byte[8]);
        var b = Frame(B, new byte[8]);
        //CSJ,variable=request,要求
        var request = new PreviewComparisonRequest(TimeSpan.Zero, A, B);

        ExpectError(request, a with { Width = 0 }, b, PreviewComparisonError.InvalidDimensions, PreviewComparisonSide.A);
        ExpectError(request, a, b with { Height = -1 }, PreviewComparisonError.InvalidDimensions, PreviewComparisonSide.B);
        ExpectError(request, a with { Width = int.MaxValue, Height = int.MaxValue }, b,
            PreviewComparisonError.InvalidDimensions, PreviewComparisonSide.A);
        ExpectError(request, a with { Pixels = default }, b,
            PreviewComparisonError.InvalidBufferLength, PreviewComparisonSide.A);
        ExpectError(request, a, b with { Pixels = new byte[9] },
            PreviewComparisonError.InvalidBufferLength, PreviewComparisonSide.B);
        ExpectError(request, a, b with { Width = 1, Height = 2 }, PreviewComparisonError.DimensionMismatch);
        ExpectError(request, a, b with { Format = PreviewPixelFormat.Pbgra32 },
            PreviewComparisonError.PixelFormatMismatch);
        ExpectError(request, a with { Format = PreviewPixelFormat.Pbgra32 }, b with { Format = PreviewPixelFormat.Pbgra32 },
            PreviewComparisonError.UnsupportedPixelFormat);
        ExpectError(request, a with { Format = (PreviewPixelFormat)42 }, b with { Format = (PreviewPixelFormat)42 },
            PreviewComparisonError.UnsupportedPixelFormat);
        ExpectError(request, a, b with { Source = B with { SnapshotId = Guid.NewGuid() } },
            PreviewComparisonError.UnexpectedFrame, PreviewComparisonSide.B);
        ExpectError(request, a with { Source = B }, b with { Source = A },
            PreviewComparisonError.UnexpectedFrame, PreviewComparisonSide.A);
        ExpectError(request, a, b with { FrameIndex = 1 },
            PreviewComparisonError.UnexpectedFrame, PreviewComparisonSide.B);
        ExpectError(request with { Timestamp = TimeSpan.FromSeconds(1) }, a, b,
            PreviewComparisonError.OutsideCommonDuration, PreviewComparisonSide.B);

        var aligned = PreviewFrameComparison.Compare(request with { Timestamp = TimeSpan.FromMilliseconds(500) },
            a with { FrameIndex = 12 }, b with { FrameIndex = 15 });
        Ensure(aligned.Metrics?.ExactlyEqual == true && aligned.Alignment.FrameA == 12 &&
            aligned.Alignment.FrameB == 15 && aligned.Alignment.Request.Timestamp.TotalMilliseconds == 500,
            "Comparison preserves the request and aligned positions");
    }

    //CSJ,method=TestCancellation,キャンセルをテストする
    private static void TestCancellation()
    {
        var a = Frame(A, new byte[8]);
        var b = Frame(B, new byte[8]);
        //CSJ,variable=cancellation,キャンセル
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            PreviewFrameComparison.Compare(new(TimeSpan.Zero, A, B), a, b, cancellation.Token);
            throw new InvalidOperationException("Cancelled comparison returned a result.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
        {
        }

        Ensure(Compare(a, b).ExactlyEqual, "Retry after cancellation");
    }

    private static void TestCancellationDuringBufferAcquisition()
    {
        //CSJ,variable=cancellation,キャンセル
        using var cancellation = new CancellationTokenSource();
        //CSJ,variable=pixels,画素列
        using var pixels = new CancelOnSpanAccess(new byte[8], cancellation);
        var a = Frame(A, new byte[8]) with { Pixels = pixels.Buffer };
        var b = Frame(B, new byte[8]);

        try
        {
            PreviewFrameComparison.Compare(new(TimeSpan.Zero, A, B), a, b, cancellation.Token);
            throw new InvalidOperationException("Comparison returned after cancellation at buffer acquisition.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
        {
            Ensure(pixels.AccessCount == 1, "Cancellation happened before the comparison acquired its buffers.");
        }
    }

    private static void TestLargeImage()
    {
        //CSJ,variable=width,幅
        const int width = 3840;
        //CSJ,variable=height,高さ
        const int height = 2160;
        var zeros = new byte[width * height * 4];
        var full = new byte[zeros.Length];
        Array.Fill(full, byte.MaxValue);
        var a = Frame(A, zeros, width) with { Height = height };
        var b = Frame(B, full, width) with { Height = height };

        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = Compare(a, b);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Ensure(result == new PreviewImageMetrics(3840, 2160, 8_294_400, 255, 8_460_288_000) &&
            result.MeanAbsoluteError == 255 && result.ChangedPixelPercentage == 100, "Full 4K difference");
        Ensure(allocated < 65_536, "Comparison allocated storage proportional to input.");
        Ensure(result == Compare(a, b), "Repeated 4K comparison produced different results.");
    }

    //CSJ,method=TestConcurrencyAsync,並行処理をテストする（非同期）
    private static async Task TestConcurrencyAsync()
    {
        var a = Frame(A, [0, 0, 0, 0], 1);
        var b = Frame(B, [0, 0, 0, 100], 1);
        var c = Frame(B, [1, 2, 3, 4], 1);
        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() => i % 2 == 0
            ? Compare(a, b) : Compare(a, c)));
        //CSJ,variable=results,結果一覧
        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < results.Length; i++)
        {
            var expected = i % 2 == 0
                ? new PreviewImageMetrics(1, 1, 1, 100, 100)
                : new PreviewImageMetrics(1, 1, 1, 4, 10);
            Ensure(results[i] == expected, "Comparison state leaked between requests.");
        }

        Ensure(a.Pixels.Span.SequenceEqual(new byte[4]) && b.Pixels.Span[3] == 100 &&
            c.Pixels.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Comparison wrote to an input buffer.");
    }

    private static PreviewFrame Frame(PreviewSource source, byte[] pixels, int width = 2) =>
        new(source, 0, width, 1, PreviewPixelFormat.Bgra32, pixels);

    //CSJ,method=Compare,比較する
    private static PreviewImageMetrics Compare(PreviewFrame a, PreviewFrame b)
    {
        var result = PreviewFrameComparison.Compare(new(TimeSpan.Zero, A, B), a, b);
        Ensure(result.Error == PreviewComparisonError.None && result.ErrorSide is null, "Unexpected failure");
        return result.Metrics ?? throw new InvalidOperationException("Missing metrics");
    }

    private static void ExpectError(PreviewComparisonRequest request, PreviewFrame a, PreviewFrame b,
        //CSJ,parameter=error,エラー
        PreviewComparisonError error, PreviewComparisonSide? side = null)
    {
        var result = PreviewFrameComparison.Compare(request, a, b);
        Ensure(result.Error == error && result.ErrorSide == side && result.Metrics is null,
            $"Expected {error}/{side}, got {result.Error}/{result.ErrorSide}");
    }

    private static void ExpectAlignment(PreviewComparisonRequest request, int? a, int? b,
        //CSJ,parameter=error,エラー
        PreviewComparisonError error = PreviewComparisonError.None, PreviewComparisonSide? side = null)
    {
        var result = PreviewFrameAlignment.Align(request);
        Ensure(result == new PreviewFrameAlignment(request, a, b, error, side), $"Alignment mismatch: {result}");
    }

    //CSJ,method=Ensure,確認する
    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
