namespace YMMProjectManager.Presentation.ViewModels;

public sealed partial class ProjectDiffViewModel
{
    public void RunSelectedSnapshotCompare()
    {
        if (SnapshotBrowser.IsCompareRunning)
        {
            SnapshotBrowser.LastCompareStatusText = "blocked";
            SnapshotBrowser.LastCompareErrorText = "Compare is already running.";
            TrackManualUiAction("CompareBlocked", "already-running");
            PersistManualValidationLog();
            return;
        }

        SnapshotBrowser.IsCompareRunning = true;
        SnapshotBrowser.LastCompareErrorText = string.Empty;
        SnapshotBrowser.LastCompareStatusText = "running (preview/manual)";
        SnapshotBrowser.LastCompareResultSummary = string.Empty;
        TrackManualUiAction("CompareStarted", "started");
        try
        {
            var compareSw = System.Diagnostics.Stopwatch.StartNew();
            var request = SnapshotBrowser.BuildCompareRequest();
            if (request is null)
            {
                SnapshotBrowser.LastCompareStatusText = "blocked";
                SnapshotBrowser.LastCompareErrorText = "Select valid old/new snapshots before compare.";
                TrackManualUiAction("CompareBlocked", "invalid-selection");
                PersistManualValidationLog();
                return;
            }

            if (!snapshotRepository.TryGetSnapshotByHash(request.OldSnapshotHash, out var oldSnapshot) ||
                !snapshotRepository.TryGetSnapshotByHash(request.NewSnapshotHash, out var newSnapshot) ||
                oldSnapshot is null || newSnapshot is null)
            {
                SnapshotBrowser.LastCompareStatusText = "no-op";
                SnapshotBrowser.LastCompareErrorText = "Snapshot body is missing in repository. Compare skipped.";
                TrackManualUiAction("CompareNoOp", "snapshot-body-missing");
                PersistManualValidationLog();
                return;
            }

            var envelope = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(
                oldSnapshot,
                newSnapshot,
                new DiffTimelineStandalonePipelineOptions(
                    OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["requestedRoute"] = "snapshot-browser-preview",
                        ["compareMode"] = "manual-preview",
                    },
                    SnapshotCache: standalonePipelineCache));
            if (!envelope.IsSuccess || envelope.Result is null)
            {
                SnapshotBrowser.LastCompareStatusText = "failed";
                SnapshotBrowser.LastCompareErrorText = string.IsNullOrWhiteSpace(envelope.FallbackReason) ? "pipeline failed" : envelope.FallbackReason;
                TrackManualUiAction("CompareFailed", SnapshotBrowser.LastCompareErrorText);
                PersistManualValidationLog();
                return;
            }

            latestCoreResult = envelope.Result.CoreResult;
            comparisonHistoryStore.Append(new DiffTimelineComparisonHistoryEntry(
                OldSnapshotHash: request.OldSnapshotHash,
                NewSnapshotHash: request.NewSnapshotHash,
                ComparedAt: DateTimeOffset.Now,
                Summary: $"manual-preview: rows={envelope.Result.CoreResult.RowSet.Rows.Count}",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cacheHit"] = envelope.CacheHit.ToString(),
                    ["source"] = envelope.SnapshotSource,
                    ["groupCount"] = envelope.Result.CoreResult.Groups.Count.ToString(),
                }));

            var uiSw = System.Diagnostics.Stopwatch.StartNew();
            visibleRowWindowStart = 0;
            visibleRowWindowSize = 500;
            MaterializeRowsForCurrentWindow(envelope.Result.CoreResult);
            DiffGroups.Clear();
            BuildGroups(envelope.Result.CoreResult.Groups);
            MatchStatisticsText = envelope.Result.CoreResult.Summary.SummaryText;
            RefreshSnapshotBrowserState("snapshot-compare");
            ApplyStandaloneFiltersAndGrouping();
            uiSw.Stop();
            lastUiUpdateDuration = uiSw.Elapsed;
            compareSw.Stop();
            lastCompareApplyDuration = compareSw.Elapsed;
            lastRenderDuration = lastFilterDuration + lastGroupingDuration + lastUiUpdateDuration;

            var d = envelope.Result.Diagnostics;
            SnapshotBrowser.LastCompareResultSummary = $"added={d.AddedCount}, removed={d.RemovedCount}, changed={d.ChangedCount}, rows={d.RowCount}, groups={d.GroupCount}, cacheHit={envelope.CacheHit}";
            SnapshotBrowser.LastCompareDiagnosticsPath = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            SnapshotBrowser.LastCompareTimestamp = DateTimeOffset.Now;
            SnapshotBrowser.LastCompareStatusText = "success (preview/manual)";
            SaveCurrentCompareSession();
            TrackManualUiAction("CompareSucceeded", SnapshotBrowser.LastCompareResultSummary);
            PersistManualValidationLog();
            NotifyMetricsRefreshCompleted(includeFilterState: false);
        }
        catch (Exception ex)
        {
            SnapshotBrowser.LastCompareStatusText = "failed";
            SnapshotBrowser.LastCompareErrorText = ex.Message;
            TrackManualUiAction("CompareFailed", ex.Message);
            PersistManualValidationLog();
            logger.Error(ex, "RunSelectedSnapshotCompare failed");
        }
        finally
        {
            SnapshotBrowser.IsCompareRunning = false;
        }
    }

    private void TrackManualUiAction(string actionType, string stateSummary)
    {
        manualUiActions.Add(new DiffTimelineManualUiAction(
            actionType,
            DateTimeOffset.Now,
            stateSummary,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["searchText"] = FilterSearchText ?? string.Empty,
                ["groupingMode"] = SelectedGroupingMode,
                ["compareSummary"] = SnapshotBrowser.CompareSummaryText,
            }));
    }

    private void PersistManualValidationLog()
    {
        var selected = SnapshotBrowser.BuildCompareRequest();
        var diagnosticsDir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        var log = new DiffTimelineManualUiValidationLog(
            SessionId: manualValidationSessionId,
            CreatedAt: DateTimeOffset.Now,
            Actions: manualUiActions.ToList(),
            SelectedOldSnapshotHash: selected?.OldSnapshotHash ?? "(none)",
            SelectedNewSnapshotHash: selected?.NewSnapshotHash ?? "(none)",
            CompareRequestSummary: SnapshotBrowser.CompareSummaryText,
            CompareSucceeded: string.Equals(SnapshotBrowser.LastCompareStatusText, "success (preview/manual)", StringComparison.Ordinal),
            BlockedOrNoOpReason: SnapshotBrowser.LastCompareStatusText is "blocked" or "no-op" ? SnapshotBrowser.LastCompareErrorText : string.Empty,
            DiagnosticsPath: SnapshotBrowser.LastCompareDiagnosticsPath,
            ExportPackagePath: string.Empty,
            LatestStatusText: SnapshotBrowser.LastCompareStatusText,
            LatestErrorText: SnapshotBrowser.LastCompareErrorText);
        LatestManualValidationLogPath = DiffTimelineManualUiValidationLogWriter.Write(diagnosticsDir, log);
        var summary = new DiffTimelineManualUiValidationSessionSummary(
            SessionId: manualValidationSessionId,
            UpdatedAt: DateTimeOffset.Now,
            CompareCount: manualUiActions.Count(x => x.ActionType.StartsWith("Compare", StringComparison.Ordinal)),
            BlockedCount: manualUiActions.Count(x => x.ActionType == "CompareBlocked"),
            NoOpCount: manualUiActions.Count(x => x.ActionType == "CompareNoOp"),
            FailureCount: manualUiActions.Count(x => x.ActionType == "CompareFailed"),
            LatestDiagnosticsPath: SnapshotBrowser.LastCompareDiagnosticsPath,
            LatestExportPath: string.Empty,
            LatestResult: SnapshotBrowser.LastCompareStatusText);
        DiffTimelineManualUiValidationLogWriter.WriteSummary(diagnosticsDir, summary);
        LatestManualValidationSummary = $"compare={summary.CompareCount}, blocked={summary.BlockedCount}, noop={summary.NoOpCount}, failed={summary.FailureCount}";
    }
}
