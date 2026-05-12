# RouteB Scene-aware History Preview Investigation

## Scope
- RouteA standalone DiffTimeline remains preserved.
- TimelineView production embedding/replacement/runtime bridge stays frozen.
- This phase is read-only investigation for current-scene context preview feasibility.

## What Was Added
- Experimental investigation probe:
  - `Presentation/Timeline/Experimental/SceneAwareHistoryPreviewProbe.cs`
- Experimental investigation VM/window:
  - `Presentation/Timeline/Experimental/ViewModels/SceneAwareHistoryPreviewInvestigationViewModel.cs`
  - `Presentation/Timeline/Experimental/Views/SceneAwareHistoryPreviewInvestigationWindow.xaml`
  - `Presentation/Timeline/Experimental/Views/SceneAwareHistoryPreviewInvestigationWindow.xaml.cs`
- Entry point from existing advanced experimental lane:
  - `ProjectDiffViewModel.OpenSceneAwareHistoryPreviewInvestigation()`
  - `ProjectDiffWindow` advanced button (`Scene-awareí≤ç∏`)

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

## Investigation Data Collected
- TimelineView type / DataContext type
- owner window / owner DataContext
- scene-like fields (name/index/frame)
- item/layer/selection count candidates
- snapshot/history candidate file presence
- reflection-based scene/timeline/selection property hints
- timeline fingerprint candidate (read-only)

## Notes
- This is not RouteB production implementation.
- No semantic diff core changes.
- No runtime mutation/input injection/command execution.
