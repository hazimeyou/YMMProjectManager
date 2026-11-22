namespace YMMProjectManager.Application.PreviewComparison;

//CSJ,record=SceneRenderObservation,シーン描画観測記録
internal readonly record struct SceneRenderObservation(SceneSnapshot A, SceneSnapshot B, Guid? EditorStateVersion);

/// <summary>Caller supplies the authorized execution context and fresh observations at each call.</summary>
//CSJ,interface=ISceneRenderContext,シーン描画コンテキストインターフェース
internal interface ISceneRenderContext
{
    //CSJ,method=Observe,観測する
    SceneRenderObservation Observe();
    //CSJ,method=Open,開く
    ISceneRenderSource Open(SceneRenderRequest request);
}

/// <summary>Adapter-owned capture scope. The response remains readable until disposal.</summary>
//CSJ,interface=ISceneRenderSource,シーン描画元インターフェース
internal interface ISceneRenderSource : IDisposable
{
    //CSJ,method=Render,描画する
    SceneRenderResponse Render(CancellationToken cancellationToken);
}

//CSJ,enum=SceneRenderFailure,シーン描画失敗
internal enum SceneRenderFailure { Busy, CleanupFailed, UnobservableState, StateChanged, HandoffRejected, ReplayMismatch }

//CSJ,class=SceneRenderException,シーン描画例外
internal sealed class SceneRenderException : InvalidOperationException
{
    public SceneRenderException(SceneRenderFailure kind, SceneHandoffRejection? rejection = null)
        : base(rejection is { } detail ? $"{kind}: {detail.Error} ({detail.Side})" : kind.ToString())
    {
        Kind = kind;
        Rejection = rejection;
    }

    //CSJ,property=Kind,種別
    public SceneRenderFailure Kind { get; }
    //CSJ,property=Rejection,拒否理由
    public SceneHandoffRejection? Rejection { get; }
}

/// <summary>One non-waiting gate per participating host session. The caller establishes thread affinity.</summary>
//CSJ,class=SceneRenderCoordinator,シーン描画調整役
internal sealed class SceneRenderCoordinator(Guid hostSessionId, TimeProvider? clock = null)
{
    //CSJ,field=sessionId,セッションID
    private readonly Guid sessionId = hostSessionId != Guid.Empty ? hostSessionId
        : throw new ArgumentException("A host session is required.", nameof(hostSessionId));
    //CSJ,field=time,時刻
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    //CSJ,field=Available,利用可能
    private const int Available = 0;
    private const int Capturing = 1;
    private const int Quarantined = 2;
    //CSJ,field=gate,排他制御
    private int gate;

    //CSJ,method=Run,実行する
    public PreviewComparisonResult Run(
        //CSJ,parameter=plan,計画
        SceneComparisonPlan plan, ISceneRenderContext host, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.A.Snapshot.HostSessionId != sessionId)
            throw Rejected(new(SceneHandoffError.HostSessionMismatch));
        int prior = Interlocked.CompareExchange(ref gate, Capturing, Available);
        if (prior != Available)
            throw new SceneRenderException(prior == Quarantined ? SceneRenderFailure.CleanupFailed : SceneRenderFailure.Busy);

        try
        {
            long start = time.GetTimestamp();
            return CapturePair(plan.Reissue(), host, timeout, start, cancellationToken);
        }
        finally
        {
            Interlocked.CompareExchange(ref gate, Available, Capturing);
        }
    }

    //CSJ,method=CapturePair,組を取得する
    private PreviewComparisonResult CapturePair(
        //CSJ,parameter=plan,計画
        SceneComparisonPlan plan, ISceneRenderContext host, TimeSpan timeout, long start, CancellationToken token)
    {
        void CheckBudget()
        {
            token.ThrowIfCancellationRequested();
            if (time.GetElapsedTime(start) >= timeout)
                throw new TimeoutException("The Scene render deadline elapsed.");
        }

        SceneRenderObservation ReadBoundState()
        {
            var observed = host.Observe();
            if (plan.CheckObserved(observed.A, observed.B) is { } rejection)
                throw Rejected(rejection);
            if (observed.EditorStateVersion is null || observed.EditorStateVersion == Guid.Empty)
                throw new SceneRenderException(SceneRenderFailure.UnobservableState);
            return observed;
        }

        CheckBudget();
        //CSJ,variable=baseline,基準
        var baseline = ReadBoundState();

        //CSJ,method=CheckState,状態を確認する
        SceneRenderObservation CheckState()
        {
            var observed = ReadBoundState();
            if (baseline.EditorStateVersion != observed.EditorStateVersion)
                throw new SceneRenderException(SceneRenderFailure.StateChanged);
            return observed;
        }

        //CSJ,method=Capture,取得する
        SceneCapturedFrame Capture(SceneComparisonPlan issued, PreviewComparisonSide side)
        {
            CheckBudget();
            CheckState();
            CheckBudget();
            var source = host.Open(side == PreviewComparisonSide.A ? issued.A : issued.B);
            return WithCleanup(() =>
            {
                CheckBudget();
                CheckState();
                CheckBudget();
                //CSJ,variable=response,応答
                var response = source.Render(token);
                CheckBudget();
                CheckState();
                CheckBudget();
                var (frame, error) = SceneCapturedFrame.Copy(issued, side, response, token);
                return frame ?? throw Rejected(new(error, side));
            }, () =>
            {
                try
                {
                    source.Dispose();
                }
                catch
                {
                    Volatile.Write(ref gate, Quarantined);
                    throw;
                }
            });
        }

        var result = WithCleanup(() =>
        {
            var a = Capture(plan, PreviewComparisonSide.A);
            var b = Capture(plan, PreviewComparisonSide.B);
            var replay = Capture(plan.Reissue(), PreviewComparisonSide.A);
            CheckBudget();
            if (!a.MatchesReplay(replay, token))
                throw new SceneRenderException(SceneRenderFailure.ReplayMismatch);

            CheckBudget();
            var observed = CheckState();
            CheckBudget();
            var compared = plan.Compare(a, b, observed.A, observed.B, token);
            if (compared.Rejection is { } rejection)
                throw Rejected(rejection);
            return compared.Result!;
        }, () => CheckState());

        CheckBudget();
        return result;
    }

    //CSJ,method=Rejected,拒否
    private static SceneRenderException Rejected(SceneHandoffRejection rejection) =>
        new(SceneRenderFailure.HandoffRejected, rejection);

    private static T WithCleanup<T>(Func<T> operation, Action cleanup)
    {
        Exception? original = null;
        try
        {
            return operation();
        }
        catch (Exception error)
        {
            original = error;
            throw;
        }
        finally
        {
            try
            {
                cleanup();
            }
            catch (Exception error) when (original is not null)
            {
                throw new AggregateException(original, error);
            }
        }
    }
}
