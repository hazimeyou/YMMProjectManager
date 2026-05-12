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

## Step 4 (TimelineFingerprint / SceneIdentity Candidate Foundation)
- Added shallow timeline fingerprint candidate generation from timeline collection candidate:
  - item count
  - item type histogram
  - text presence histogram
  - stable text + SHA256 hash
- Added scene identity candidate synthesis:
  - scene name/index candidate (when available)
  - fallback identity through fingerprint hash
  - confidence scoring
- Added fingerprint safety fields:
  - max scanned items (200)
  - actual scanned items
  - getter error count
  - path/text-body excluded from hash

## Step 5 (Snapshot / History Matching Foundation)
- Added read-only history source scan over diagnostics roots.
- Added shallow JSON metadata extraction with safety limits:
  - maxDepth=5
  - maxProperties=2000
  - maxArrayItemsPerArray=50
- Added runtime fingerprint vs history metadata matching candidate scoring.
- Added summary outputs:
  - source/read success counts
  - metadata/match candidate counts
  - best match score/confidence
  - historyLinkFeasible
- Existing snapshot/history schemas are not modified.

## Step 5.5 Actual Runtime Result (2026-05-13)
- Probe file:
  - `C:\Users\yu-za-hazimeyou\Desktop\YukkuriMovieMaker_v4_Lite\diagnostics\scene-aware-history-preview-probe-20260513-013944.json`
- sceneDetected:
  - `True`
- runtime fingerprint stableHash:
  - `51DE41A4C89943588D6E03CF8D3C6BF5D40BC6F3BBB8BA940D64FD4B568277C0`
- itemCount:
  - `21`
- actualItemsScanned:
  - `21`
- sceneIdentityCandidate.confidence:
  - `High`
- historyMatching block:
  - not present in this runtime probe JSON
- bestHistoryMatchCandidate:
  - not present
- missing fields summary:
  - `historyMatching.*` fields are unavailable in this measured probe output, so `sourceCount / metadataCandidateCount / matchCandidateCount / bestMatchConfidence / historyLinkFeasible` cannot be determined from this file.

Decision:
- proceed to `Step 5.5b: Snapshot/History Metadata Gap Analysis`
- reason: branching conditions for Step 6 require `historyLinkFeasible=True` and `bestMatchConfidence>=Medium`, but those measured fields are currently unavailable in the runtime output used for this check.

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
