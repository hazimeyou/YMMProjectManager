namespace YMMProjectManager.Presentation.Timeline;

public static class PureTimelineDiagnostics
{
    private static int initializeCount;
    private static int disposeCount;
    private static int disposeFailureCount;
    private static int activeHostCount;
    private static int experimentalYmmHostSuccessCount;
    private static int experimentalYmmHostFailureCount;

    public static int InitializeCount => initializeCount;
    public static int DisposeCount => disposeCount;
    public static int DisposeFailureCount => disposeFailureCount;
    public static int ActiveHostCount => activeHostCount;
    public static int ExperimentalYmmHostSuccessCount => experimentalYmmHostSuccessCount;
    public static int ExperimentalYmmHostFailureCount => experimentalYmmHostFailureCount;

    public static void IncrementInitializeCount() => Interlocked.Increment(ref initializeCount);
    public static void IncrementDisposeCount() => Interlocked.Increment(ref disposeCount);
    public static void IncrementDisposeFailureCount() => Interlocked.Increment(ref disposeFailureCount);
    public static void IncrementActiveHostCount() => Interlocked.Increment(ref activeHostCount);
    public static void DecrementActiveHostCount() => Interlocked.Decrement(ref activeHostCount);
    public static void IncrementExperimentalYmmHostSuccessCount() => Interlocked.Increment(ref experimentalYmmHostSuccessCount);
    public static void IncrementExperimentalYmmHostFailureCount() => Interlocked.Increment(ref experimentalYmmHostFailureCount);
}
