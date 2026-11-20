namespace YMMProjectManager.Application.PreviewComparison;

/// <summary>Tokens issued and re-observed by the trusted host boundary. Contains no native references.</summary>
internal readonly record struct SceneSnapshot(
    Guid HostSessionId,
    Guid ProjectId,
    Guid ProjectRevisionId,
    Guid SceneId,
    Guid SceneLifetimeId,
    PreviewSource Source)
{
    public bool IsBound => HostSessionId != Guid.Empty && ProjectId != Guid.Empty &&
        ProjectRevisionId != Guid.Empty && SceneId != Guid.Empty && SceneLifetimeId != Guid.Empty;

    public SceneHandoffError CheckObserved(SceneSnapshot observed)
    {
        if (HostSessionId != observed.HostSessionId)
            return SceneHandoffError.HostSessionMismatch;
        if (ProjectId != observed.ProjectId)
            return SceneHandoffError.ProjectMismatch;
        if (ProjectRevisionId != observed.ProjectRevisionId)
            return SceneHandoffError.ProjectRevisionMismatch;
        if (SceneId != observed.SceneId)
            return SceneHandoffError.SnapshotMismatch;
        if (SceneLifetimeId != observed.SceneLifetimeId)
            return SceneHandoffError.SceneLifetimeMismatch;
        return Source == observed.Source ? SceneHandoffError.None : SceneHandoffError.SnapshotMismatch;
    }
}

internal enum SceneHandoffError
{
    None,
    AlignmentRejected,
    InvalidBinding,
    HostSessionMismatch,
    ProjectMismatch,
    ProjectRevisionMismatch,
    SameScene,
    SceneLifetimeMismatch,
    SnapshotMismatch,
    OperationMismatch,
    FrameMismatch,
    InvalidDimensions,
    FrameTooLarge,
    UnsupportedPixelFormat,
    InvalidStride,
    InvalidBufferLength,
}

internal readonly record struct SceneHandoffRejection(SceneHandoffError Error, PreviewComparisonSide? Side = null);

internal readonly record struct SceneRenderRequest(
    Guid OperationId,
    SceneSnapshot Snapshot,
    TimeSpan Timestamp,
    int FrameIndex,
    int Width,
    int Height);

internal sealed class SceneComparisonPlan
{
    private readonly PreviewComparisonRequest comparison;

    private SceneComparisonPlan(PreviewFrameAlignment alignment, SceneSnapshot a, SceneSnapshot b, int width, int height)
    {
        comparison = alignment.Request;
        A = new(Guid.NewGuid(), a, comparison.Timestamp, alignment.FrameA!.Value, width, height);
        B = new(Guid.NewGuid(), b, comparison.Timestamp, alignment.FrameB!.Value, width, height);
    }

    public SceneRenderRequest A { get; }
    public SceneRenderRequest B { get; }

    public static (SceneComparisonPlan? Plan, PreviewFrameAlignment Alignment, SceneHandoffRejection? Rejection) Prepare(
        TimeSpan timestamp, SceneSnapshot a, SceneSnapshot b, int width, int height, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var alignment = PreviewFrameAlignment.Align(new(timestamp, a.Source, b.Source));
        if (alignment.Error != PreviewComparisonError.None)
            return (null, alignment, new(SceneHandoffError.AlignmentRejected, alignment.ErrorSide));

        var rejection = CheckPair(a, b);
        if (rejection is not null)
            return (null, alignment, rejection);

        var sizeError = SceneCapturedFrame.CheckSize(width, height);
        return sizeError == SceneHandoffError.None
            ? (new(alignment, a, b, width, height), alignment, null)
            : (null, alignment, new(sizeError));
    }

    private static SceneHandoffRejection? CheckPair(SceneSnapshot a, SceneSnapshot b)
    {
        if (!a.IsBound)
            return new(SceneHandoffError.InvalidBinding, PreviewComparisonSide.A);
        if (!b.IsBound)
            return new(SceneHandoffError.InvalidBinding, PreviewComparisonSide.B);
        if (a.HostSessionId != b.HostSessionId)
            return new(SceneHandoffError.HostSessionMismatch);
        if (a.ProjectId != b.ProjectId)
            return new(SceneHandoffError.ProjectMismatch);
        if (a.ProjectRevisionId != b.ProjectRevisionId)
            return new(SceneHandoffError.ProjectRevisionMismatch);
        if (a.SceneId == b.SceneId || a.SceneLifetimeId == b.SceneLifetimeId)
            return new(SceneHandoffError.SameScene);

        return null;
    }

    /// <summary>Repeat against current host observations before publishing the result.</summary>
    public SceneHandoffRejection? CheckObserved(SceneSnapshot a, SceneSnapshot b)
    {
        var error = A.Snapshot.CheckObserved(a);
        if (error != SceneHandoffError.None)
            return new(error, PreviewComparisonSide.A);

        error = B.Snapshot.CheckObserved(b);
        return error == SceneHandoffError.None ? null : new(error, PreviewComparisonSide.B);
    }

    public (PreviewComparisonResult? Result, SceneHandoffRejection? Rejection) Compare(
        SceneCapturedFrame a,
        SceneCapturedFrame b,
        SceneSnapshot observedA,
        SceneSnapshot observedB,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (a.Request != A)
            return (null, new(SceneHandoffError.OperationMismatch, PreviewComparisonSide.A));
        if (b.Request != B)
            return (null, new(SceneHandoffError.OperationMismatch, PreviewComparisonSide.B));

        var rejection = CheckObserved(observedA, observedB);
        if (rejection is not null)
            return (null, rejection);

        return (SceneCapturedFrame.Compare(comparison, a, b, cancellationToken), null);
    }
}
