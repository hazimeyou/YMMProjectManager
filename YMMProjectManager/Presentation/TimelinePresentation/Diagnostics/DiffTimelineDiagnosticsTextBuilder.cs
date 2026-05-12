using YMMProjectManager.Presentation.ViewModels;

namespace YMMProjectManager.Presentation.TimelinePresentation.Diagnostics;

internal static class DiffTimelineDiagnosticsTextBuilder
{
    public static string BuildVirtualizationRecommendation(bool virtualizationRecommended)
        => virtualizationRecommended
            ? "大きな比較結果です。表示が重くなる可能性があります。仮想化表示の利用を推奨します。"
            : "仮想化表示の推奨は現在不要です。";

    public static string BuildCompactRenderDiagnostics(
        DiffTimelineVirtualizationState state,
        TimeSpan renderDuration,
        TimeSpan filterDuration)
        => $"行 {state.RowCount:N0} / グループ {state.GroupCount:N0} / 描画 {renderDuration.TotalMilliseconds:F0}ms / フィルター {filterDuration.TotalMilliseconds:F0}ms";

    public static string BuildRowWindowSummary(int displayedRowCount, int totalAvailableRowCount, int deferredRowCount)
        => $"表示中: {displayedRowCount:N0} / {totalAvailableRowCount:N0} 行 (遅延: {deferredRowCount:N0})";

    public static string BuildDetails(
        DiffTimelineVirtualizationState state,
        DiffTimelineHeavyProjectDiagnostics heavy,
        DiffTimelineProjectionCacheStats? projectionStats,
        bool isLargeResultMode,
        string largeResultModeReason,
        int visibleRowWindowStart,
        int displayedRowCount,
        int totalAvailableRowCount,
        TimeSpan renderDuration,
        TimeSpan filterDuration,
        TimeSpan groupingDuration,
        TimeSpan compareApplyDuration,
        TimeSpan uiUpdateDuration)
    {
        var reasonText = heavy.Reasons.Count == 0 ? "(none)" : string.Join(", ", heavy.Reasons);
        return $"Render={renderDuration.TotalMilliseconds:F1}ms, Filter={filterDuration.TotalMilliseconds:F1}ms, Grouping={groupingDuration.TotalMilliseconds:F1}ms, CompareApply={compareApplyDuration.TotalMilliseconds:F1}ms, UIUpdate={uiUpdateDuration.TotalMilliseconds:F1}ms\n" +
               $"VisibleRows~{state.VisibleRowEstimate:N0}, EstimatedVisuals~{state.EstimatedVisualCount:N0}, EstimatedMemory~{state.EstimatedMemoryUsageBytes / 1024.0 / 1024.0:F2}MB\n" +
               $"HeavyProjectDetected={heavy.HeavyProjectDetected}, VirtualizationRecommended={heavy.VirtualizationRecommended}, Reasons={reasonText}\n" +
               $"ProjectionCache={projectionStats?.CachedProjectionCount ?? 0}, Materialized={projectionStats?.MaterializedRowCount ?? 0}, Reuse={projectionStats?.ProjectionReuseCount ?? 0}, Deferred={projectionStats?.DeferredProjectionCount ?? 0}\n" +
               $"LargeResultMode={isLargeResultMode}, Reason={largeResultModeReason}, Window={visibleRowWindowStart}-{visibleRowWindowStart + displayedRowCount}/{totalAvailableRowCount}";
    }
}
