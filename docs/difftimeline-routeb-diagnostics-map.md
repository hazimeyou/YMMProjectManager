# RouteB Diagnostics Map

## Probe
- `scene-aware-history-preview-probe-*.json`

## Summary
- `scene-aware-history-preview-summary-*.json`

## Report
- `scene-aware-history-preview-report.md`

## Major Blocks
- `WindowScan`
- `BestYmmTimelineCandidate`
- `SurfaceInventory`
- `TimelineFingerprintDetails`
- `SceneIdentityCandidate`
- `HistoryMatching`
- `HistoryPreview`
- `RouteADetailHandoff`
- `RouteADetailHandoffGap`
- `RouteAHandoffMetadata`
- `SnapshotPairResolution`
- `RouteAOpenReadiness`
- `SceneAwareMetadata`
- `RouteBInvestigationRc`
- `RouteBInvestigationReadiness`
- `PreviewFeatureGate`
- `PreviewFeatureReadiness`
- `PreviewUiConsolidation`
- `HeavyProjectHeuristics`
- `PreviewPerformanceDiagnostics`
- `PreviewListSafety`
- `PreviewVirtualization`
- `RouteBFinalInvestigationRc`
- `RouteBFinalReadiness`

## Step 15 Additions
- `PreviewUiIntegration`
- `PreviewUiReadModel`
- `PreviewUiSafetySummary`

### PreviewUiIntegration
- `uiMode=PreviewCandidate`
- `diagnosticsCollapsed=true`
- `userFacingSections=[CurrentScene, RelatedHistory, MatchReason, RouteADetailPreview, DiagnosticsSafety]`
- `viewerWired=false`
- `openMode=ReadOnlyDryRun`
- `defaultDisabled=true`
- `fallbackPreserved=true`

## Step 16-17 Additions
- `RouteADetailViewerOpenReadiness`
- `RouteADetailViewerOpenSafety`
- `RouteADetailViewerOpenResult`

### RouteADetailViewerOpenSafety
- `viewerWired=true`
- `openMode=ReadOnlySandbox`
- `manualOnly=true`
- `readOnly=true`
- `allowDiffApply=false`
- `allowHistoryRestore=false`
- `allowRuntimeMutation=false`

## Step 18.5 Diagnostics Synchronization
- `RouteADetailViewerOpenResult` now reflects manual button execution result.
- After `RouteA詳細を開く（読み取り専用）`, probe/summary/report are regenerated with latest open result.
- Success path:
  - `openAttempted=true`
  - `openSucceeded=true`
  - `fallbackToDryRun=false`
  - `openedWindowType=ProjectDiffWindow`
- Failure path:
  - `openAttempted=true`
  - `openSucceeded=false`
  - `fallbackToDryRun=true`
  - `errorMessage` populated
- Compatibility clarification:
  - `PreviewVirtualization` and `RouteBFinalInvestigationRc` include RC2 compatibility snapshot values and current values side-by-side.

## Step 27-32 Additions
- `RouteBHeavyRuntimeValidation`
- `RouteBHeavyPerformanceSummary`
- `RouteBPreviewScalabilityReadiness`

### RouteBHeavyRuntimeValidation
- `isHeavyProject`
- `heavyReasons[]`
- `recommendedVirtualization`
- `historySourceCount`
- `snapshotRepositoryCount`
- `timelineItemCount`
- `estimatedHistoryJsonBytes`

### RouteBHeavyPerformanceSummary
- `totalProbeMs`
- `historyMatchingMs`
- `snapshotPairResolutionMs`
- `previewGenerationMs`
- `readiness`

### RouteBPreviewScalabilityReadiness
- `previewItemLimit`
- `displayedCandidates`
- `truncated`
- `deferredDetailMaterialization`
- `lightweightProjection`
- `readiness`
- `warnings[]`
