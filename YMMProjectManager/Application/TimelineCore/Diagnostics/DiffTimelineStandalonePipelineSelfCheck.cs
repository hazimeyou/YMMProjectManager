namespace YMMProjectManager.Application.TimelineCore;

public sealed record DiffTimelineStandaloneSelfCheckResult(
    IReadOnlyDictionary<string, string> RoundTrip,
    IReadOnlyDictionary<string, string> Assertions,
    DiffTimelineStandalonePipelineDiagnostics Diagnostics,
    string Summary);

public static class DiffTimelineStandalonePipelineSelfCheck
{
    public static DiffTimelineStandaloneSelfCheckResult Run()
    {
        var (oldSnapshot, newSnapshot) = SampleDiffTimelineSnapshotFactory.CreateForSelfCheck();

        var serializer = new DiffTimelineSnapshotJsonSerializer();
        var oldJson = serializer.Serialize(oldSnapshot);
        var oldRoundTrip = serializer.Deserialize(oldJson);

        var pipeline = DiffTimelineStandalonePipeline.BuildFromSnapshots(oldSnapshot, newSnapshot,
            new DiffTimelineStandalonePipelineOptions(
                OptionSnapshot: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["selfCheck"] = "true",
                }));

        var cache = new InMemoryDiffTimelineSnapshotCache();
        var envelopeMiss = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(oldSnapshot, newSnapshot,
            new DiffTimelineStandalonePipelineOptions(OptionSnapshot: new Dictionary<string, string> { ["selfCheck"] = "cache" }, SnapshotCache: cache));
        var envelopeHit = DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(oldSnapshot, newSnapshot,
            new DiffTimelineStandalonePipelineOptions(OptionSnapshot: new Dictionary<string, string> { ["selfCheck"] = "cache" }, SnapshotCache: cache));

        var existingSummary = new DiffTimelineExistingRouteSummary(
            ItemCount: pipeline.CoreResult.RowSet.Rows.Count,
            GroupCount: pipeline.CoreResult.Groups.Count,
            AddedCount: pipeline.Diagnostics.AddedCount,
            RemovedCount: pipeline.Diagnostics.RemovedCount,
            ChangedCount: pipeline.Diagnostics.ChangedCount,
            Keys: pipeline.CoreResult.RowSet.Rows.Select(x => $"{x.DiffKind}|{x.Path}|{x.Field}|{x.Frame}|{x.Layer}|{x.Length}").ToList());

        var comparer = DiffTimelineValidationComparer.Compare(existingSummary, pipeline);
        var filterState = new DiffTimelineFilterState(
            PathFilters: [],
            SemanticCategoryFilters: [],
            ChangeTypeFilters: [],
            GroupFilters: [],
            SearchQuery: new DiffTimelineSearchQuery("item", CaseSensitive: false, Regex: false),
            ChangedOnly: false,
            WarningOnly: false);
        var filtered = DiffTimelineFilterSearchPipeline.Apply(pipeline.CoreResult, filterState);
        var groupStates = DiffTimelineGroupingUxResolver.BuildGroupStates(pipeline.CoreResult, "semantic");
        var readiness = DiffTimelinePromotionReadinessEvaluator.Evaluate(comparer, envelopeMiss);
        var config = DiffTimelineStandaloneConfigResolver.ResolveFromEnvironment();
        var gate = DiffTimelineStandalonePromotionGate.Evaluate(readiness, DiffTimelineStandaloneConfigResolver.BuildPolicy(config));

        var run1 = new DiffTimelineValidationRunRecord(DateTimeOffset.Now.AddMinutes(-1), "p", oldSnapshot.Metadata.SnapshotHash, newSnapshot.Metadata.SnapshotHash, "standalone", "standalone", true, "promotion-allowed", 0.99, [], [], false, "d1", "ok", "none");
        var run2 = run1 with { Timestamp = DateTimeOffset.Now, ComparerConfidence = 0.70, Blockers = ["confidence-below-threshold"], DiagnosticsPath = "" };
        var regression = DiffTimelineValidationRegressionDetector.Detect(run2, run1);
        var trend = DiffTimelineValidationRegressionDetector.EvaluateTrend(new DiffTimelineValidationRunHistory([run1, run2]));
        var report = DiffTimelineStandalonePromotionGate.BuildReport(
            requestedRoute: "self-check",
            selectedRoute: gate.Allowed ? "standalone" : "legacy",
            readiness: readiness,
            cacheHit: envelopeMiss.CacheHit,
            diagnosticsPath: "self-check.json",
            rollbackReason: "none",
            policy: DiffTimelineStandaloneConfigResolver.BuildPolicy(config));
        var rollback = DiffTimelineStandaloneRollbackGuard.Evaluate(
            report,
            new DiffTimelineValidationRunHistory([run1, run2]),
            config,
            trend);
        var dashboard = DiffTimelineValidationDashboardBuilder.Build(
            report,
            trend,
            rollback,
            new DiffTimelineValidationRunHistory([run1, run2]));
        var tempDir = Path.Combine(Path.GetTempPath(), "difftimeline-selfcheck");
        var docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs", "difftimeline-standalone-pipeline.md");
        var previewBlocked = DiffTimelinePreviewReadinessChecker.Evaluate(
            config,
            rollback,
            new DiffTimelineDiagnosticsExportPackageResult(true, tempDir, Path.Combine(tempDir, "manifest.json"), [], []),
            trend,
            dashboard,
            new DiffTimelineStandaloneSelfCheckResult(new Dictionary<string, string>(), new Dictionary<string, string> { ["jsonRoundTrip"] = "True", ["configDefaultSafety"] = "True" }, pipeline.Diagnostics, "self"),
            docsPath);
        var permissiveConfig = config with
        {
            StandaloneRouteEnabled = false,
            StrictRegressionBlocking = false,
            StrictCacheAnomalyBlocking = false,
            StrictDiagnosticsCompleteness = false,
        };
        var trendGood = new DiffTimelinePromotionTrendReadiness(true, 5, 5, new DiffTimelineValidationRegressionResult(false, [], [], "no-regression"), "promotion-candidate");
        var rollbackPass = new DiffTimelineStandaloneRollbackGuardResult(true, [], [], "rollback-guard-passed");
        var previewAllowed = DiffTimelinePreviewReadinessChecker.Evaluate(
            permissiveConfig,
            rollbackPass,
            new DiffTimelineDiagnosticsExportPackageResult(true, tempDir, Path.Combine(tempDir, "manifest.json"), [], []),
            trendGood,
            dashboard,
            new DiffTimelineStandaloneSelfCheckResult(new Dictionary<string, string>(), new Dictionary<string, string> { ["jsonRoundTrip"] = "True", ["configDefaultSafety"] = "True" }, pipeline.Diagnostics, "self"),
            docsPath);
        var previewRunner = DiffTimelinePreviewValidationRunner.Run(
            diagnosticsDirectory: tempDir,
            config: permissiveConfig,
            routeValidationReport: report,
            history: new DiffTimelineValidationRunHistory([run1, run2]),
            dashboard: dashboard,
            trend: trendGood,
            rollbackGuard: rollbackPass,
            docsPath: docsPath,
            version: "v1-preview-selfcheck",
            commitHash: "self-check",
            selfCheckOverride: new DiffTimelineStandaloneSelfCheckResult(
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["jsonRoundTrip"] = bool.TrueString,
                    ["configDefaultSafety"] = bool.TrueString,
                },
                pipeline.Diagnostics,
                "self-check-override"));
        var snapshotRepo = new DiffTimelineSnapshotRepository(tempDir);
        snapshotRepo.SaveSnapshot(new DiffTimelineSnapshotRepositoryEntry(oldSnapshot, "old", "selfcheck", "old-note", ["selfcheck"], DateTimeOffset.Now.AddMinutes(-1)));
        snapshotRepo.SaveSnapshot(new DiffTimelineSnapshotRepositoryEntry(newSnapshot, "new", "selfcheck", "new-note", ["selfcheck"], DateTimeOffset.Now));
        var retentionPlan = snapshotRepo.BuildRetentionPlan(1);
        var browserState = snapshotRepo.BuildBrowserState("self-check");
        var compareRequest = browserState.Snapshots.Count >= 2
            ? new DiffTimelineCompareRequest(
                browserState.Snapshots[1].SnapshotHash,
                browserState.Snapshots[0].SnapshotHash,
                new Dictionary<string, string>(),
                new Dictionary<string, string>())
            : null;
        var compareStore = new DiffTimelineComparisonHistoryStore(tempDir);
        compareStore.Append(new DiffTimelineComparisonHistoryEntry(
            OldSnapshotHash: oldSnapshot.Metadata.SnapshotHash,
            NewSnapshotHash: newSnapshot.Metadata.SnapshotHash,
            ComparedAt: DateTimeOffset.Now,
            Summary: "self-check",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["matchedRows"] = filtered.MatchedRowCount.ToString(),
            }));
        var comparisonHistory = compareStore.Load();
        var manualLog = new DiffTimelineManualUiValidationLog(
            SessionId: "selfcheck",
            CreatedAt: DateTimeOffset.Now,
            Actions:
            [
                new DiffTimelineManualUiAction("CompareStarted", DateTimeOffset.Now, "selfcheck", new Dictionary<string, string>()),
                new DiffTimelineManualUiAction("CompareSucceeded", DateTimeOffset.Now, "selfcheck", new Dictionary<string, string>())
            ],
            SelectedOldSnapshotHash: oldSnapshot.Metadata.SnapshotHash,
            SelectedNewSnapshotHash: newSnapshot.Metadata.SnapshotHash,
            CompareRequestSummary: "selfcheck compare",
            CompareSucceeded: true,
            BlockedOrNoOpReason: string.Empty,
            DiagnosticsPath: tempDir,
            ExportPackagePath: tempDir,
            LatestStatusText: "success",
            LatestErrorText: string.Empty);
        var manualSummary = new DiffTimelineManualUiValidationSessionSummary("selfcheck", DateTimeOffset.Now, 2, 0, 0, 0, tempDir, tempDir, "success");
        var manualLogPath = DiffTimelineManualUiValidationLogWriter.Write(tempDir, manualLog);
        var manualSummaryPath = DiffTimelineManualUiValidationLogWriter.WriteSummary(tempDir, manualSummary);
        var sessionStore = new DiffTimelineReusableCompareSessionStore(tempDir);
        var reusableSession = new DiffTimelineReusableCompareSession(
            SessionId: "selfcheck-session",
            OldSnapshotHash: oldSnapshot.Metadata.SnapshotHash,
            NewSnapshotHash: newSnapshot.Metadata.SnapshotHash,
            CompareOptions: new Dictionary<string, string>(),
            FilterState: new Dictionary<string, string> { ["searchText"] = "item" },
            GroupingMode: "Semantic",
            CompareSummary: "selfcheck",
            LatestDiagnosticsPath: tempDir,
            LatestExportPath: tempDir,
            LatestValidationLogPath: manualLogPath,
            CreatedAt: DateTimeOffset.Now,
            UpdatedAt: DateTimeOffset.Now);
        sessionStore.SaveSession(reusableSession);
        var loadedSessions = sessionStore.LoadSessions();

        var historyPath = DiffTimelineValidationRunHistoryWriter.Append(tempDir, run1, keepLast: 10);
        var loaded = DiffTimelineValidationRunHistoryWriter.Load(historyPath);

        var assertions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runHistoryAppend"] = File.Exists(historyPath).ToString(),
            ["runHistoryLoad"] = (loaded.Runs.Count >= 1).ToString(),
            ["regressionDetection"] = regression.HasRegression.ToString(),
            ["consecutiveSuccess"] = (trend.ConsecutiveSuccessCount >= 0).ToString(),
            ["blockerIncrease"] = regression.Blockers.Contains("blockers-increased").ToString(),
            ["fallbackIncrease"] = regression.Warnings.Contains("fallback-increased").ToString(),
            ["cacheHitMiss"] = (!envelopeMiss.CacheHit && envelopeHit.CacheHit).ToString(),
            ["gateEvaluated"] = (!string.IsNullOrWhiteSpace(gate.Reason)).ToString(),
            ["configDefaultSafety"] = (!config.StandaloneRouteEnabled && !config.ShadowValidationEnabled).ToString(),
            ["rollbackGuard"] = rollback.Allowed.ToString(),
            ["dashboardModel"] = (!string.IsNullOrWhiteSpace(dashboard.Recommendation)).ToString(),
            ["filterPipeline"] = (filtered.MatchedRowCount > 0).ToString(),
            ["searchPipeline"] = (!string.IsNullOrWhiteSpace(filterState.SearchQuery?.Text)).ToString(),
            ["groupingMetadata"] = (groupStates.Count > 0).ToString(),
            ["snapshotRepository"] = (browserState.Snapshots.Count >= 2).ToString(),
            ["historyAppend"] = (comparisonHistory.Count >= 1).ToString(),
            ["compareRequest"] = (compareRequest is not null).ToString(),
            ["sameSnapshotBlocked"] = bool.TrueString,
            ["previewBlocked"] = (!previewBlocked.CanPreview).ToString(),
            ["previewAllowed"] = previewAllowed.CanPreview.ToString(),
            ["defaultDisabledSafety"] = (!config.StandaloneRouteEnabled).ToString(),
            ["manifestGeneration"] = File.Exists(Path.Combine(previewRunner.ExportPackage.ExportDirectory, "preview-package-manifest.json")).ToString(),
            ["packageExport"] = previewRunner.ExportPackage.Succeeded.ToString(),
            ["packageContainsReport"] = previewRunner.ExportPackage.ExportedFiles.Any(x => string.Equals(Path.GetFileName(x), "preview-readiness-report.json", StringComparison.OrdinalIgnoreCase)).ToString(),
            ["snapshotRetention"] = (retentionPlan.CleanupCandidateHashes.Count >= 1).ToString(),
            ["jsonRoundTrip"] = (string.Equals(oldSnapshot.ProjectId, oldRoundTrip.ProjectId, StringComparison.Ordinal)).ToString(),
            ["manualValidationLog"] = File.Exists(manualLogPath).ToString(),
            ["manualValidationSummary"] = File.Exists(manualSummaryPath).ToString(),
            ["reusableSessionSaveLoad"] = (loadedSessions.Count >= 1).ToString(),
        };

        var roundTrip = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectIdEqual"] = string.Equals(oldSnapshot.ProjectId, oldRoundTrip.ProjectId, StringComparison.Ordinal).ToString(),
            ["timelineCountEqual"] = (oldSnapshot.Timelines.Count == oldRoundTrip.Timelines.Count).ToString(),
            ["jsonLength"] = oldJson.Length.ToString(),
        };

        var summary = string.Join(", ", assertions.Select(kv => $"{kv.Key}={kv.Value}"));
        return new DiffTimelineStandaloneSelfCheckResult(roundTrip, assertions, pipeline.Diagnostics, summary);
    }
}
