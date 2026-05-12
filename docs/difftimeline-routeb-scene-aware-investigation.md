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

## Step 3 (TimelineViewModel Surface Inventory)
- Added public surface inventory scanner for:
  - best timeline candidate DataContext
  - owner window DataContext
  - `Application.Current.MainWindow`
  - `Application.Current.MainWindow.DataContext`
- Added property-level diagnostics:
  - category classification (Scene/Timeline/Collection/Layer/Frame/Selection/etc.)
  - safe getter read attempt / success / error recording
  - value kind, preview, collection count
  - shallow sample item types/toString (max 3)
  - frame/layer/text/start/end/duration-like property hints
- Added best-candidate synthesis:
  - `bestSceneCandidate`
  - `bestTimelineCollectionCandidate`
- Added summary counters:
  - scanned object count
  - property/readable counts
  - scene/collection/frame/selection candidate counts
  - getter error count

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
