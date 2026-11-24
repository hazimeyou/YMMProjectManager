namespace YMMProjectManager.Application.PreviewComparison;

/// <summary>Tokens issued and re-observed by the trusted host boundary. Contains no native references.</summary>
//CSJ,record=SceneSnapshot,シーンスナップショット
internal readonly record struct SceneSnapshot(
    Guid HostSessionId,
    Guid ProjectId,
    Guid ProjectRevisionId,
    Guid SceneId,
    Guid SceneLifetimeId,
    PreviewSource Source)
{
    //CSJ,property=IsBound,関連付け済みか
    public bool IsBound => HostSessionId != Guid.Empty && ProjectId != Guid.Empty &&
        ProjectRevisionId != Guid.Empty && SceneId != Guid.Empty && SceneLifetimeId != Guid.Empty;

    //CSJ,method=CheckObserved,観測結果を照合する
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

//CSJ,enum=SceneHandoffError,シーン比較の準備エラー
internal enum SceneHandoffError
{
    None,
    //CSJ,field=AlignmentRejected,整合拒否
    AlignmentRejected,
    //CSJ,field=InvalidBinding,無効バインディング
    InvalidBinding,
    //CSJ,field=HostSessionMismatch,ホストセッション不一致
    HostSessionMismatch,
    //CSJ,field=ProjectMismatch,プロジェクト不一致
    ProjectMismatch,
    //CSJ,field=ProjectRevisionMismatch,プロジェクト版不一致
    ProjectRevisionMismatch,
    //CSJ,field=SameScene,同一シーン
    SameScene,
    SceneLifetimeMismatch,
    //CSJ,field=SnapshotMismatch,スナップショット不一致
    SnapshotMismatch,
    //CSJ,field=OperationMismatch,操作不一致
    OperationMismatch,
    //CSJ,field=FrameMismatch,フレーム不一致
    FrameMismatch,
    InvalidDimensions,
    FrameTooLarge,
    //CSJ,field=UnsupportedPixelFormat,未対応画素形式
    UnsupportedPixelFormat,
    //CSJ,field=InvalidStride,無効行ストライド
    InvalidStride,
    //CSJ,field=InvalidBufferLength,無効バッファー長さ
    InvalidBufferLength,
    InvalidPremultipliedPixel,
}

//CSJ,record=SceneHandoffRejection,シーン引き継ぎの拒否理由
internal readonly record struct SceneHandoffRejection(SceneHandoffError Error, PreviewComparisonSide? Side = null);

//CSJ,record=SceneRenderRequest,シーン描画要求
internal readonly record struct SceneRenderRequest(
    Guid OperationId,
    SceneSnapshot Snapshot,
    TimeSpan Timestamp,
    int FrameIndex,
    int Width,
    int Height);

//CSJ,class=SceneComparisonPlan,シーン比較計画
internal sealed class SceneComparisonPlan
{
    //CSJ,field=comparison,比較
    private readonly PreviewComparisonRequest comparison;

    private SceneComparisonPlan(PreviewFrameAlignment alignment, SceneSnapshot a, SceneSnapshot b, int width, int height)
    {
        comparison = alignment.Request;
        A = new(Guid.NewGuid(), a, comparison.Timestamp, alignment.FrameA!.Value, width, height);
        B = new(Guid.NewGuid(), b, comparison.CurrentTime!.Value, alignment.FrameB!.Value, width, height);
    }

    private SceneComparisonPlan(PreviewComparisonRequest comparison, SceneRenderRequest a, SceneRenderRequest b)
    {
        this.comparison = comparison;
        A = a;
        B = b;
    }

    internal SceneComparisonPlan Reissue() =>
        new(comparison, A with { OperationId = Guid.NewGuid() }, B with { OperationId = Guid.NewGuid() });

    public SceneRenderRequest A { get; }
    public SceneRenderRequest B { get; }

    //CSJ,method=Prepare,準備する
    public static (SceneComparisonPlan? Plan, PreviewFrameAlignment Alignment, SceneHandoffRejection? Rejection) Prepare(
        //CSJ,parameter=timestamp,タイムスタンプ
        TimeSpan timestamp, SceneSnapshot a, SceneSnapshot b, int width, int height, CancellationToken cancellationToken = default) =>
        PrepareCore(new(timestamp, a.Source, b.Source), a, b, width, height, requireSameRevision: true, cancellationToken);

    internal static (SceneComparisonPlan? Plan, PreviewFrameAlignment Alignment, SceneHandoffRejection? Rejection) PrepareProjects(
        TimeSpan timestamp, ComparisonTiming timing, SceneSnapshot original, SceneSnapshot current, int width, int height,
        CancellationToken token = default) =>
        PrepareCore(timing.Bind(timestamp, original.Source, current.Source), original, current, width, height,
            requireSameRevision: false, token);

    private static (SceneComparisonPlan? Plan, PreviewFrameAlignment Alignment, SceneHandoffRejection? Rejection) PrepareCore(
        PreviewComparisonRequest request, SceneSnapshot a, SceneSnapshot b, int width, int height,
        bool requireSameRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=alignment,整合
        var alignment = PreviewFrameAlignment.Align(request);
        if (alignment.Error != PreviewComparisonError.None)
            return (null, alignment, new(SceneHandoffError.AlignmentRejected, alignment.ErrorSide));

        var rejection = CheckPair(a, b, requireSameRevision);
        if (rejection is not null)
            return (null, alignment, rejection);

        //CSJ,variable=sizeError,サイズエラー
        var sizeError = SceneCapturedFrame.CheckSize(width, height);
        return sizeError == SceneHandoffError.None
            ? (new(alignment, a, b, width, height), alignment, null)
            : (null, alignment, new(sizeError));
    }

    //CSJ,method=CheckPair,組を確認する
    private static SceneHandoffRejection? CheckPair(SceneSnapshot a, SceneSnapshot b, bool requireSameRevision)
    {
        if (!a.IsBound)
            return new(SceneHandoffError.InvalidBinding, PreviewComparisonSide.A);
        if (!b.IsBound)
            return new(SceneHandoffError.InvalidBinding, PreviewComparisonSide.B);
        if (a.HostSessionId != b.HostSessionId)
            return new(SceneHandoffError.HostSessionMismatch);
        if (requireSameRevision && a.ProjectId != b.ProjectId)
            return new(SceneHandoffError.ProjectMismatch);
        if (requireSameRevision && a.ProjectRevisionId != b.ProjectRevisionId)
            return new(SceneHandoffError.ProjectRevisionMismatch);
        if (a.SceneLifetimeId == b.SceneLifetimeId ||
            (a.ProjectId == b.ProjectId && a.ProjectRevisionId == b.ProjectRevisionId && a.SceneId == b.SceneId))
            return new(SceneHandoffError.SameScene);

        return null;
    }

    /// <summary>Repeat against current host observations before publishing the result.</summary>
    //CSJ,method=CheckObserved,観測結果を照合する
    public SceneHandoffRejection? CheckObserved(SceneSnapshot a, SceneSnapshot b)
    {
        //CSJ,variable=error,エラー
        var error = A.Snapshot.CheckObserved(a);
        if (error != SceneHandoffError.None)
            return new(error, PreviewComparisonSide.A);

        error = B.Snapshot.CheckObserved(b);
        return error == SceneHandoffError.None ? null : new(error, PreviewComparisonSide.B);
    }

    //CSJ,method=Compare,比較する
    public (PreviewComparisonResult? Result, SceneHandoffRejection? Rejection) Compare(
        SceneCapturedFrame a,
        SceneCapturedFrame b,
        SceneSnapshot observedA,
        SceneSnapshot observedB,
        //CSJ,parameter=cancellationToken,キャンセルトークン
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
