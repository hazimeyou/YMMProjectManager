using System.Buffers;
using YMMProjectManager.Application.PreviewComparison;

//CSJ,class=SceneComparisonHandoffTests,シーン比較引き継ぎテスト
internal static class SceneComparisonHandoffTests
{
    //CSJ,field=Session,セッション
    private static readonly Guid Session = Guid.NewGuid();
    //CSJ,field=Project,プロジェクト
    private static readonly Guid Project = Guid.NewGuid();
    //CSJ,field=Revision,版
    private static readonly Guid Revision = Guid.NewGuid();
    //CSJ,field=Configuration,構成
    private static readonly Guid Configuration = Guid.NewGuid();
    private static readonly SceneSnapshot A = Snapshot(24, 48);
    private static readonly SceneSnapshot B = Snapshot(30, 30);
    //CSJ,field=Timestamp,タイムスタンプ
    private static readonly TimeSpan Timestamp = TimeSpan.FromMilliseconds(500);

    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync()
    {
        await CaseAsync("TL-2A binding and alignment admission", TestPreparation);
        await CaseAsync("TL-2A render receipt correlation and frame identity", TestReceipt);
        await CaseAsync("TL-2A fresh observations before comparison", TestFreshness);
        await CaseAsync("TL-2A stride normalization, alpha bytes and ownership", TestPixels);
        await CaseAsync("TL-2A malformed and oversized render output", TestInvalidOutput);
        await CaseAsync("TL-2A cancellation after buffer acquisition and retry", TestCancellation);
        await CaseAsync("TL-2A repeat and operation isolation", TestRepeated);
        await Program.RunNamedCaseAsync("TL-2A concurrent host-independent requests", TestConcurrentAsync);
    }

    private static Task CaseAsync(string name, Action action) => Program.RunNamedCaseAsync(name, () =>
    {
        action();
        return Task.CompletedTask;
    });

    private static SceneSnapshot Snapshot(int fps, int count) =>
        new(Session, Project, Revision, Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Configuration, new(fps, 1), count));

    private static SceneComparisonPlan Plan()
    {
        var result = SceneComparisonPlan.Prepare(Timestamp, A, B, 1, 2);
        return result.Plan ?? throw new InvalidOperationException($"Expected a plan, got {result.Rejection}.");
    }

    private static void TestPreparation()
    {
        //CSJ,variable=plan,計画
        var plan = Plan();
        Assert(plan.A.Timestamp == Timestamp && plan.B.Timestamp == Timestamp &&
            plan.A.FrameIndex == 12 && plan.B.FrameIndex == 15, "Expected containing frames at 500 ms.");
        Assert(plan.A.Snapshot == A && plan.B.Snapshot == B &&
            plan.A.OperationId != Guid.Empty && plan.B.OperationId != Guid.Empty &&
            plan.A.OperationId != plan.B.OperationId, "Each render leg needs a distinct identity.");
        var end = SceneComparisonPlan.Prepare(TimeSpan.FromSeconds(1), A, B, 1, 2);
        Assert(end.Plan is null && end.Alignment.FrameA == 24 && end.Alignment.FrameB is null &&
            end.Alignment.Error == PreviewComparisonError.OutsideCommonDuration &&
            end.Rejection == new SceneHandoffRejection(SceneHandoffError.AlignmentRejected, PreviewComparisonSide.B),
            "A render operation was issued for an out-of-range pair.");

        ExpectPlanError(B with { HostSessionId = Guid.Empty }, SceneHandoffError.InvalidBinding, PreviewComparisonSide.B);
        ExpectPlanError(B with { HostSessionId = Guid.NewGuid() }, SceneHandoffError.HostSessionMismatch);
        ExpectPlanError(B with { ProjectId = Guid.NewGuid() }, SceneHandoffError.ProjectMismatch);
        ExpectPlanError(B with { ProjectRevisionId = Guid.NewGuid() }, SceneHandoffError.ProjectRevisionMismatch);
        ExpectPlanError(B with { SceneId = A.SceneId }, SceneHandoffError.SameScene);
        ExpectPlanError(B with { SceneLifetimeId = A.SceneLifetimeId }, SceneHandoffError.SameScene);
        Assert(SceneComparisonPlan.Prepare(Timestamp, A, B, 0, 2).Rejection?.Error ==
            SceneHandoffError.InvalidDimensions, "Invalid output dimensions.");
        Assert(SceneComparisonPlan.Prepare(Timestamp, A, B, int.MaxValue, int.MaxValue).Rejection?.Error ==
            SceneHandoffError.FrameTooLarge, "Overflow-safe size check.");
    }

    private static void ExpectPlanError(SceneSnapshot b, SceneHandoffError error, PreviewComparisonSide? side = null)
    {
        var result = SceneComparisonPlan.Prepare(Timestamp, A, b, 1, 2);
        Assert(result.Plan is null && result.Rejection == new SceneHandoffRejection(error, side), $"Expected {error}.");
    }

    //CSJ,method=TestReceipt,照合記録をテストする
    private static void TestReceipt()
    {
        //CSJ,variable=plan,計画
        var plan = Plan();
        var a = Response(plan.A, new byte[8]);
        Assert(SceneCapturedFrame.Copy(plan, PreviewComparisonSide.B, a) == (null, SceneHandoffError.OperationMismatch),
            "An A receipt was accepted in the B slot.");
        Assert(SceneCapturedFrame.Copy(plan, (PreviewComparisonSide)42, a) == (null, SceneHandoffError.OperationMismatch),
            "An unknown render slot was accepted.");
        ExpectCopyError(plan, a with { OperationId = plan.B.OperationId }, SceneHandoffError.OperationMismatch);
        ExpectCopyError(plan, a with { FrameIndex = 13 }, SceneHandoffError.FrameMismatch);
        ExpectCopyError(plan, a with { ObservedSnapshot = A with { SceneLifetimeId = Guid.NewGuid() } },
            SceneHandoffError.SceneLifetimeMismatch);
        ExpectCopyError(plan, a with { ObservedSnapshot = A with { Source = A.Source with { SnapshotId = Guid.NewGuid() } } },
            SceneHandoffError.SnapshotMismatch);
        ExpectCopyError(plan, a with { ObservedSnapshot = A with { Source = A.Source with { FrameRate = new(48, 2) } } },
            SceneHandoffError.SnapshotMismatch);
        ExpectCopyError(plan, a with { ObservedSnapshot = A with { ProjectRevisionId = Guid.NewGuid() } },
            SceneHandoffError.ProjectRevisionMismatch);
        ExpectCopyError(plan, a with { ObservedSnapshot = A with { HostSessionId = Guid.NewGuid() } },
            SceneHandoffError.HostSessionMismatch);
    }

    private static void TestFreshness()
    {
        //CSJ,variable=plan,計画
        var plan = Plan();
        var a = Copy(plan, PreviewComparisonSide.A, Response(plan.A, new byte[8]));
        var b = Copy(plan, PreviewComparisonSide.B, Response(plan.B, new byte[8]));
        Assert(plan.Compare(a, b, A, B).Result?.Metrics?.ExactlyEqual == true, "Live pair should compare.");
        var stale = plan.Compare(a, b, A, B with { SceneLifetimeId = Guid.NewGuid() });
        Assert(stale.Result is null &&
            stale.Rejection == new SceneHandoffRejection(SceneHandoffError.SceneLifetimeMismatch, PreviewComparisonSide.B),
            "Stale lifetime must be rejected even after successful copy.");
        Assert(plan.CheckObserved(A with { ProjectRevisionId = Guid.NewGuid() }, B) ==
            new SceneHandoffRejection(SceneHandoffError.ProjectRevisionMismatch, PreviewComparisonSide.A),
            "Freshness check must support final validation before publication.");
    }

    //CSJ,method=TestPixels,画素列をテストする
    private static void TestPixels()
    {
        //CSJ,variable=plan,計画
        var plan = Plan();
        byte[] padded = [11, 22, 33, 0, 211, 212, 1, 2, 3, 128, 213, 214];
        byte[] packed = [11, 22, 33, 0, 1, 2, 3, 128];
        var a = Copy(plan, PreviewComparisonSide.A, Response(plan.A, padded) with { Stride = 6 });
        var b = Copy(plan, PreviewComparisonSide.B, Response(plan.B, packed));
        Assert(padded.AsSpan().SequenceEqual(new byte[] { 11, 22, 33, 0, 211, 212, 1, 2, 3, 128, 213, 214 }),
            "Normalization wrote to the borrowed buffer.");
        Array.Fill(padded, byte.MaxValue);
        Array.Clear(packed);
        var result = plan.Compare(a, b, A, B).Result;
        Assert(result?.Metrics?.ExactlyEqual == true && result.Alignment.FrameA == 12 && result.Alignment.FrameB == 15,
            "Padding and mutations of borrowed storage must not affect comparison.");

        var opaque = Copy(plan, PreviewComparisonSide.B, Response(plan.B, [11, 22, 33, 0, 1, 2, 3, 255]));
        Assert(plan.Compare(a, opaque, A, B).Result?.Metrics == new PreviewImageMetrics(1, 2, 1, 127, 127),
            "Normalization must preserve alpha and stored RGB.");
        var transparent = Copy(plan, PreviewComparisonSide.B, Response(plan.B, [0, 22, 33, 0, 1, 2, 3, 128]));
        Assert(plan.Compare(a, transparent, A, B).Result?.Metrics == new PreviewImageMetrics(1, 2, 1, 11, 11),
            "Normalization must preserve RGB in fully transparent pixels.");
    }

    //CSJ,method=TestInvalidOutput,無効出力をテストする
    private static void TestInvalidOutput()
    {
        //CSJ,variable=plan,計画
        var plan = Plan();
        //CSJ,variable=response,応答
        var response = Response(plan.A, new byte[8]);
        ExpectCopyError(plan, response with { Format = PreviewPixelFormat.Pbgra32 }, SceneHandoffError.UnsupportedPixelFormat);
        ExpectCopyError(plan, response with { Format = (PreviewPixelFormat)42 }, SceneHandoffError.UnsupportedPixelFormat);
        ExpectCopyError(plan, response with { Width = 0 }, SceneHandoffError.InvalidDimensions);
        ExpectCopyError(plan, response with { Width = 2, Height = 1 }, SceneHandoffError.InvalidDimensions);
        ExpectCopyError(plan, response with { Stride = -4 }, SceneHandoffError.InvalidStride);
        ExpectCopyError(plan, response with { Stride = 3 }, SceneHandoffError.InvalidStride);
        ExpectCopyError(plan, response with { Stride = int.MaxValue }, SceneHandoffError.FrameTooLarge);
        ExpectCopyError(plan, response with { Pixels = new byte[9] }, SceneHandoffError.InvalidBufferLength);
        ExpectCopyError(plan, response with { Stride = 6, Pixels = new byte[10] }, SceneHandoffError.InvalidBufferLength);
    }

    //CSJ,method=TestCancellation,キャンセルをテストする
    private static void TestCancellation()
    {
        //CSJ,variable=cancellation,キャンセル
        using var cancellation = new CancellationTokenSource();
        //CSJ,variable=plan,計画
        var plan = Plan();
        using var borrowed = new CancelOnSpanAccess(new byte[8], cancellation);
        //CSJ,variable=response,応答
        var response = Response(plan.A, []) with { Pixels = borrowed.Buffer };
        ExpectCancellation(() => SceneCapturedFrame.Copy(plan, PreviewComparisonSide.A, response, cancellation.Token), cancellation.Token);
        Assert(borrowed.AccessCount == 1, "Cancellation must happen after buffer acquisition.");
        ExpectCancellation(() => SceneComparisonPlan.Prepare(Timestamp, A, B, 1, 2, cancellation.Token),
            cancellation.Token);
        var a = Copy(plan, PreviewComparisonSide.A, Response(plan.A, new byte[8]));
        var b = Copy(plan, PreviewComparisonSide.B, Response(plan.B, new byte[8]));
        ExpectCancellation(() => plan.Compare(a, b, A, B, cancellation.Token), cancellation.Token);
        Assert(plan.Compare(a, b, A, B).Result?.Metrics?.ExactlyEqual == true, "Cancellation contaminated retry.");
    }

    private static void ExpectCancellation(Action operation, CancellationToken token)
    {
        try
        {
            operation();
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == token)
        {
        }
    }

    private static void TestRepeated()
    {
        var first = Plan();
        var second = Plan();
        var a = Copy(first, PreviewComparisonSide.A, Response(first.A, new byte[8]));
        var b = Copy(first, PreviewComparisonSide.B, Response(first.B, new byte[8]));
        Assert(first.A.OperationId != second.A.OperationId && first.B.OperationId != second.B.OperationId,
            "Repeated plans reused operation IDs.");
        Assert(first.Compare(a, b, A, B) == first.Compare(a, b, A, B), "Repeat comparison was inconsistent.");
        Assert(second.Compare(a, b, A, B).Rejection ==
            new SceneHandoffRejection(SceneHandoffError.OperationMismatch, PreviewComparisonSide.A),
            "An earlier plan's frame was accepted.");
        Assert(first.Compare(a, a, A, B).Rejection ==
            new SceneHandoffRejection(SceneHandoffError.OperationMismatch, PreviewComparisonSide.B),
            "A response was used in the B slot.");
    }

    //CSJ,method=TestConcurrentAsync,並行をテストする（非同期）
    private static async Task TestConcurrentAsync()
    {
        using var rendezvous = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(index => Task.Factory.StartNew(() =>
        {
            //CSJ,variable=plan,計画
            var plan = Plan();
            var a = Copy(plan, PreviewComparisonSide.A, Response(plan.A, new byte[8]));
            if (!rendezvous.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Comparison tasks did not overlap.");
            //CSJ,variable=image,画像
            byte[] image = [0, 0, 0, (byte)index, 0, 0, 0, 0];
            var b = Copy(plan, PreviewComparisonSide.B, Response(plan.B, image));
            return (plan.A.OperationId, plan.B.OperationId, plan.Compare(a, b, A, B).Result?.Metrics);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        //CSJ,variable=results,結果一覧
        var results = await Task.WhenAll(tasks);
        Assert(results.SelectMany(item => new[] { item.Item1, item.Item2 }).Distinct().Count() == 4,
            "Concurrent plans reused render operation IDs.");
        for (int i = 0; i < results.Length; i++)
        {
            Assert(results[i].Metrics == new PreviewImageMetrics(1, 2, i == 0 ? 0 : 1, (byte)i, i),
                "Concurrent results leaked across requests.");
        }
    }

    private static SceneRenderResponse Response(SceneRenderRequest request, byte[] pixels) =>
        new(request.OperationId, request.Snapshot, request.FrameIndex, 1, 2, 4, PreviewPixelFormat.Bgra32, pixels);

    //CSJ,method=Copy,コピーする
    private static SceneCapturedFrame Copy(SceneComparisonPlan plan, PreviewComparisonSide side, SceneRenderResponse response)
    {
        var result = SceneCapturedFrame.Copy(plan, side, response);
        return result.Frame ?? throw new InvalidOperationException($"Copy rejected: {result.Error}.");
    }

    private static void ExpectCopyError(SceneComparisonPlan plan, SceneRenderResponse response, SceneHandoffError error)
    {
        Assert(SceneCapturedFrame.Copy(plan, PreviewComparisonSide.A, response) == (null, error), $"Expected copy rejection {error}.");
    }

    //CSJ,method=Assert,検査する
    private static void Assert(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}

//CSJ,class=CancelOnSpanAccess,スパン参照時のキャンセル
internal sealed class CancelOnSpanAccess(byte[] pixels, CancellationTokenSource cancellation) : MemoryManager<byte>
{
    //CSJ,property=AccessCount,アクセス件数
    public int AccessCount { get; private set; }

    //CSJ,property=Buffer,バッファー
    public ReadOnlyMemory<byte> Buffer => CreateMemory(pixels.Length);

    public override Span<byte> GetSpan()
    {
        AccessCount++;
        cancellation.Cancel();
        return pixels;
    }

    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
    public override void Unpin() => throw new NotSupportedException();
    //CSJ,method=Dispose,破棄する
    protected override void Dispose(bool disposing) { }
}
