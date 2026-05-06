namespace YMMProjectManager.Presentation.Timeline;

public static class PureTimelineDiagnostics
{
    private static int initializeCount;
    private static int disposeCount;
    private static int disposeFailureCount;
    private static int activeHostCount;
    private static int experimentalYmmHostSuccessCount;
    private static int experimentalYmmHostFailureCount;
    private static int timelineReflectionFailureCount;
    private static int experimentalReadyCount;
    private static long timelineReflectionProbeMs;
    private static int timelineReflectionAssemblyCount;
    private static int timelineReflectionTypeFoundCount;
    private static long timelineConstructorBindingMs;
    private static int timelineConstructorCandidateCount;
    private static int timelineConstructorBindableCount;
    private static int timelineGenerationReadinessScore;
    private static int timelineGenerationBlockingReasonCount;
    private static int timelineViewModelGenerationAttemptCount;
    private static int timelineViewModelGenerationSuccessCount;
    private static int timelineViewModelGenerationFailureCount;
    private static int timelineViewModelDisposeAttemptCount;
    private static int timelineViewModelDisposeSuccessCount;
    private static int timelineViewModelDisposeFailureCount;
    private static long timelineViewModelGenerationAttemptMs;
    private static long timelineViewModelDisposeMs;

    public static int InitializeCount => initializeCount;
    public static int DisposeCount => disposeCount;
    public static int DisposeFailureCount => disposeFailureCount;
    public static int ActiveHostCount => activeHostCount;
    public static int ExperimentalYmmHostSuccessCount => experimentalYmmHostSuccessCount;
    public static int ExperimentalYmmHostFailureCount => experimentalYmmHostFailureCount;
    public static int TimelineReflectionFailureCount => timelineReflectionFailureCount;
    public static int ExperimentalReadyCount => experimentalReadyCount;
    public static long TimelineReflectionProbeMs => Interlocked.Read(ref timelineReflectionProbeMs);
    public static int TimelineReflectionAssemblyCount => timelineReflectionAssemblyCount;
    public static int TimelineReflectionTypeFoundCount => timelineReflectionTypeFoundCount;
    public static long TimelineConstructorBindingMs => Interlocked.Read(ref timelineConstructorBindingMs);
    public static int TimelineConstructorCandidateCount => timelineConstructorCandidateCount;
    public static int TimelineConstructorBindableCount => timelineConstructorBindableCount;
    public static int TimelineGenerationReadinessScore => timelineGenerationReadinessScore;
    public static int TimelineGenerationBlockingReasonCount => timelineGenerationBlockingReasonCount;
    public static int TimelineViewModelGenerationAttemptCount => timelineViewModelGenerationAttemptCount;
    public static int TimelineViewModelGenerationSuccessCount => timelineViewModelGenerationSuccessCount;
    public static int TimelineViewModelGenerationFailureCount => timelineViewModelGenerationFailureCount;
    public static int TimelineViewModelDisposeAttemptCount => timelineViewModelDisposeAttemptCount;
    public static int TimelineViewModelDisposeSuccessCount => timelineViewModelDisposeSuccessCount;
    public static int TimelineViewModelDisposeFailureCount => timelineViewModelDisposeFailureCount;
    public static long TimelineViewModelGenerationAttemptMs => Interlocked.Read(ref timelineViewModelGenerationAttemptMs);
    public static long TimelineViewModelDisposeMs => Interlocked.Read(ref timelineViewModelDisposeMs);

    public static void IncrementInitializeCount() => Interlocked.Increment(ref initializeCount);
    public static void IncrementDisposeCount() => Interlocked.Increment(ref disposeCount);
    public static void IncrementDisposeFailureCount() => Interlocked.Increment(ref disposeFailureCount);
    public static void IncrementActiveHostCount() => Interlocked.Increment(ref activeHostCount);
    public static void DecrementActiveHostCount() => Interlocked.Decrement(ref activeHostCount);
    public static void IncrementExperimentalYmmHostSuccessCount() => Interlocked.Increment(ref experimentalYmmHostSuccessCount);
    public static void IncrementExperimentalYmmHostFailureCount() => Interlocked.Increment(ref experimentalYmmHostFailureCount);
    public static void IncrementTimelineReflectionFailureCount() => Interlocked.Increment(ref timelineReflectionFailureCount);
    public static void IncrementExperimentalReadyCount() => Interlocked.Increment(ref experimentalReadyCount);

    public static void UpdateTimelineReflectionMetrics(long probeMs, int assemblyCount, int typeFoundCount)
    {
        Interlocked.Exchange(ref timelineReflectionProbeMs, probeMs);
        Interlocked.Exchange(ref timelineReflectionAssemblyCount, assemblyCount);
        Interlocked.Exchange(ref timelineReflectionTypeFoundCount, typeFoundCount);
    }

    public static void UpdateTimelineConstructorBindingMetrics(
        long bindingMs,
        int candidateCount,
        int bindableCount,
        int readinessScore,
        int blockingReasonCount)
    {
        Interlocked.Exchange(ref timelineConstructorBindingMs, bindingMs);
        Interlocked.Exchange(ref timelineConstructorCandidateCount, candidateCount);
        Interlocked.Exchange(ref timelineConstructorBindableCount, bindableCount);
        Interlocked.Exchange(ref timelineGenerationReadinessScore, readinessScore);
        Interlocked.Exchange(ref timelineGenerationBlockingReasonCount, blockingReasonCount);
    }

    public static void UpdateTimelineViewModelGenerationMetrics(YmmTimelineGenerationAttemptResult result)
    {
        Interlocked.Increment(ref timelineViewModelGenerationAttemptCount);
        if (result.Succeeded)
        {
            Interlocked.Increment(ref timelineViewModelGenerationSuccessCount);
        }
        else
        {
            Interlocked.Increment(ref timelineViewModelGenerationFailureCount);
        }

        Interlocked.Exchange(ref timelineViewModelGenerationAttemptMs, result.GenerationAttemptMs);

        if (result.DisposeAttempted)
        {
            Interlocked.Increment(ref timelineViewModelDisposeAttemptCount);
            if (result.DisposeSucceeded)
            {
                Interlocked.Increment(ref timelineViewModelDisposeSuccessCount);
            }
            else
            {
                Interlocked.Increment(ref timelineViewModelDisposeFailureCount);
            }

            Interlocked.Exchange(ref timelineViewModelDisposeMs, result.DisposeMs);
        }
    }
}
