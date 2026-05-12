# RouteB Scene-aware History Preview Investigation

## Scope
- RouteA standalone DiffTimeline remains preserved.
- TimelineView production embedding/replacement/runtime bridge stays frozen.
- This phase is read-only investigation for current-scene context preview feasibility.

## Step 1
- Confirmed mis-detection: RouteA `DiffTimelineView` was being detected instead of YMM runtime timeline.
- Typical result:
  - `timelineViewType=YMMProjectManager.Presentation.Views.DiffTimelineView`
  - `timelineVmType=YMMProjectManager.Presentation.ViewModels.DiffTimelineViewModel`

## Step 2 (YMM Timeline Candidate Scan)
- Added `Application.Current.Windows` scanning.
- Added exclusion rules:
  - `ProjectDiffWindow`
  - `SceneAwareHistoryPreviewInvestigationWindow`
  - investigation/helper windows under `YMMProjectManager.*`
- Added visual-tree based timeline candidate scan on non-excluded windows.
- Added candidate scoring + confidence.
- Added RouteA DiffTimeline exclusion for candidate list.
- Added safe public getter read logs for scene/timeline-related fields.

## Safety
- read-only only
- no input injection
- no runtime mutation
- no command execution
- RouteA preserved

## Output Files
Probe execution writes into `diagnostics`:
- `scene-aware-history-preview-probe-*.json`
- `scene-aware-history-preview-summary-*.json`
- `scene-aware-history-preview-report.md`

## Probe Guarantees
- `defaultDisabled = true`
- `fallbackPreserved = true`
- `routeAPreserved = true`
- `timelineViewIntegrationFrozen = true`
- `productionEmbedding = false`
- `runtimeMutation = false`
- `inputInjection = false`
