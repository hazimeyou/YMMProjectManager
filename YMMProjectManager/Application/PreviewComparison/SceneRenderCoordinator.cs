namespace YMMProjectManager.Application.PreviewComparison;

internal readonly record struct SceneRenderObservation(SceneSnapshot A, SceneSnapshot B, Guid? EditorStateVersion);

/// <summary>Caller supplies the authorized execution context and fresh observations at each call.</summary>
internal interface ISceneRenderContext
{
    SceneRenderObservation Observe();
    ISceneRenderSource Open(SceneRenderRequest request);
}

/// <summary>Adapter-owned capture scope. The response remains readable until disposal.</summary>
internal interface ISceneRenderSource : IDisposable
{
    SceneRenderResponse Render(CancellationToken cancellationToken);
}

internal enum SceneRenderFailure { Busy, CleanupFailed, UnobservableState, StateChanged, HandoffRejected, ReplayMismatch }

internal sealed class SceneRenderException : InvalidOperationException
{
    public SceneRenderException(SceneRenderFailure kind, SceneHandoffRejection? rejection = null)
        : base(rejection is { } detail ? $"{kind}: {detail.Error} ({detail.Side})" : kind.ToString())
    {
        Kind = kind;
        Rejection = rejection;
    }

    public SceneRenderFailure Kind { get; }
    public SceneHandoffRejection? Rejection { get; }
}

/// <summary>One non-waiting gate per participating host session. The caller establishes thread affinity.</summary>
internal sealed class SceneRenderCoordinator(Guid hostSessionId, TimeProvider? clock = null)
{
    private readonly Guid sessionId = hostSessionId != Guid.Empty ? hostSessionId
        : throw new ArgumentException("A host session is required.", nameof(hostSessionId));
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private const int Available = 0;
    private const int Capturing = 1;
    private const int Quarantined = 2;
    private int gate;

    public PreviewComparisonResult Run(
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

    private PreviewComparisonResult CapturePair(
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
        var baseline = ReadBoundState();

        SceneRenderObservation CheckState()
        {
            var observed = ReadBoundState();
            if (baseline.EditorStateVersion != observed.EditorStateVersion)
                throw new SceneRenderException(SceneRenderFailure.StateChanged);
            return observed;
        }

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
