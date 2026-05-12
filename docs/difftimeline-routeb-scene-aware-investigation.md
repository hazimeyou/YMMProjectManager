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

## Step 5.5b (Snapshot / History Metadata Gap Analysis)

### 1) Step5 Output Presence
- Step5 output present in runtime probe JSON:
  - `No` (latest measured file does not include `historyMatching`, `historySources`, `historyMatchCandidates`, `bestHistoryMatchCandidate`)
- Code-side Step5 fields:
  - `Yes` (`SceneAwareHistoryPreviewProbeResult` includes Step5 properties in current source)

Interpretation:
- Most likely split is `D (runtime DLL/output generation timing mismatch)` from the Step5.5 assumptions:
  - source contains Step5 fields
  - measured probe file was generated before the latest Step5-enabled runtime execution cycle

### 2) DLL Reflection Check
- Local build output DLL timestamp:
  - `2026-05-13T02:09:20.7005529+09:00`
- YMM Lite plugin DLL timestamp:
  - `2026-05-13T02:09:20.7005529+09:00`
- Conclusion:
  - plugin DLL copy itself is up to date; however, measured probe files used for Step5.5 were generated at `01:39` and therefore are older runtime outputs.

### 3) History Source Roots / Files (read-only observed)
- Observed diagnostics root:
  - `C:\Users\yu-za-hazimeyou\Desktop\YukkuriMovieMaker_v4_Lite\diagnostics`
- Confirmed files relevant to matching foundation:
  - `difftimeline-comparison-history.json`
  - `difftimeline-snapshot-repository.json`
  - `manual-ui-validation-summary-*.json`
  - `scene-aware-history-preview-probe-*.json`
  - `scene-aware-history-preview-summary-*.json`

### 4) Metadata Availability (current measured state)
- Runtime side (from latest measured probe):
  - `stableHash`: available
  - `itemCount`: available
  - `itemTypeHistogram`: available
  - `layerCount`: not reliable (often null/unknown)
  - `minFrame` / `maxFrame`: not reliable (often null/unknown)
  - `sceneName` / `sceneIndex`: partially unknown in candidate path
- History side (from current measured probe output block):
  - `historyMatching`: unavailable in measured JSON (cannot evaluate match counters/confidence from this run)

### 5) Gap Classification
- Critical:
  - cannot evaluate `bestMatchConfidence` / `historyLinkFeasible` because Step5 result block is absent in measured probe JSON
- Important:
  - missing stable scene identity fields (`sceneName`, `sceneIndex`) in runtime candidate path for robust non-hash fallback
- Optional:
  - richer historical linkage metadata (`compareSessionId`, normalized source timestamp mapping)

### 6) Decision
- Step 6 ready:
- `No` (evidence insufficient in measured runtime output)
- Recommended next step:
  1. Re-run Scene-aware investigation once with the latest deployed DLL (post-02:09 build) to regenerate probe/summary/report.
  2. Confirm that Step5 block appears:
     - `historyMatching`
     - `historySources`
     - `historyMatchCandidates`
     - `bestHistoryMatchCandidate`
  3. If still absent, return to Step5 implementation validation (serializer/result wiring path).

### 7) Re-run Result Update (2026-05-13 02:13 JST)
- Latest probe:
  - `scene-aware-history-preview-probe-20260513-021349.json`
- Step5 output presence:
  - `Yes` (`HistoryMatching`, `HistorySources`, `HistoryMatchCandidates`, `BestHistoryMatchCandidate` all present)
- Runtime summary:
  - `sceneDetected=True`
  - `stableHash=51DE41A4C89943588D6E03CF8D3C6BF5D40BC6F3BBB8BA940D64FD4B568277C0`
  - `itemCount/actualItemsScanned=21/21`
- History matching summary:
  - `sourceCount=11`
  - `readSucceededCount=11`
  - `metadataCandidateCount=11`
  - `matchCandidateCount=9`
  - `bestMatchScore=170`
  - `bestMatchConfidence=High`
  - `historyLinkFeasible=True`
  - `bestHistoryMatchCandidate=scene-aware-history-preview-summary-20260513-013944.json (Summary)`
  - `matchReasons=stableHash exact match, sceneName match, itemCount match, recent file`

Updated Decision:
- Step 6 ready:
  - `Yes`
- Recommended next step:
  - proceed to `Step 6: Scene-aware History List Preview`

## Step 6 (Scene-aware History List Preview)
- Scope:
  - investigation window only
  - read-only preview only
  - no production embedding
- Added:
  - score-sorted preview list (`max 20`)
  - selected preview item detail panel
  - Step 6 diagnostics fields:
    - `historyPreview.previewItemCount`
    - `historyPreview.bestPreviewItemScore`
    - `historyPreview.bestPreviewItemConfidence`
    - `historyPreview.hasHighConfidenceMatch`
    - `historyPreview.routeADetailHandoffPrepared`
    - `historyPreviewItems[]`
- RouteA detail diff handoff:
  - prepared as candidate metadata only
  - not executed in Step 6

## Step 7A (RouteA Detail Diff Handoff Foundation)
- Added read-only handoff candidate extraction from selected history preview item source JSON.
- Added `routeADetailHandoff` into probe/summary output.
- Added investigation UI section:
  - prepared/canOpen/reason
  - snapshotId / compareSessionId
  - availableFields / missingFields / warnings
- Scope:
  - dry-run only
  - `Open RouteA Detail Diff` remains disabled in Step 7A
  - no runtime mutation / no snapshot restore / no RouteA replacement

## Step 7A.5 (RouteA Handoff Metadata Gap Fix)
- Added handoff gap classification output:
  - `criticalMissingFields`
  - `importantMissingFields`
  - `optionalMissingFields`
  - `recommendedSchemaFields`
- Added schema proposal focus for reliable handoff:
  - `compareSessionId` (required)
  - `oldSnapshotId/newSnapshotId` (required)
  - `previewWorkspaceStatePath` (recommended)
  - `sceneAwareStableHash` (recommended)
  - `sourceKind` (recommended)

## Auto Progress 2 Result (Latest Probe Gate + Branch)
- Latest available probe at analysis time was older than Step7A/7A.5 output schema and did not include:
  - `historyPreview`
  - `routeADetailHandoff`
  - `routeADetailHandoffGap`
- Branch decision:
  - treated as output-gap gate, then advanced to Step7B foundation (non-blocking path).

## Step 7B (Scene-aware Metadata Save Foundation)
- Added optional non-breaking block:
  - `sceneAwareMetadata`
- Current target outputs:
  - RouteB probe JSON
  - RouteB summary JSON
- Block content:
  - `schemaVersion`
  - `source` (`route`, `investigation`, `defaultDisabled`, `readOnly`)
  - `sceneIdentity`
  - `timelineFingerprint`
  - `routeAHandoff`
  - `privacy`
- Privacy defaults:
  - `fullPathExcluded=true`
  - `textBodyExcluded=true`
  - `projectPathHashOnly=true`

## Step 7B.5 (RouteA Handoff Metadata Formalization)
- Added `routeAHandoffMetadata` as a formal optional block in RouteB probe/summary outputs.
- Added `routeAOpenReadiness` block for non-destructive open decision diagnostics.
- Updated `CanOpen` rule:
  - `compareSessionId` present
  - OR `snapshotPair(old/new)` present
  - OR `previewWorkspaceStatePath + comparisonHistoryPath` present
  - and confidence `>= Medium`
- Compatibility:
  - optional + nullable + non-breaking
  - read-only/default-disabled preserved

## Step 7B.6 (Resolve Snapshot Pair from History Repository)
- Added `snapshotPairResolution` output block.
- Resolution flow:
  - read latest `*comparison-history*.json`
  - extract `OldSnapshotHash/NewSnapshotHash`
  - match with `Snapshot.Metadata.SnapshotHash` in `*snapshot-repository*.json`
  - backfill `routeAHandoffMetadata.snapshotPair`
- Goal:
  - allow `CanOpen=True` even without `compareSessionId` when snapshot pair resolves.

## Step 7B.7 (Snapshot Pair Resolver Refinement)
- Fixed resolver to support both JSON root kinds:
  - array root
  - object root (`value/entries/history/items/results` and fallback single object)
- Added stage-aware diagnostics and debug output:
  - root kinds
  - entry counts
  - selected entry index/reason
  - exception stage
- Added safer parsing for old/new snapshot hash variants and nested object paths.

## Step 8 (Safe RouteA Detail Preview Open Foundation)
- Added guarded open foundation in investigation window:
  - button enabled only when readiness/safety conditions pass
  - user-click only (no auto-open)
  - read-only dry-run open attempt text logging
- Current mode:
  - viewer wired: not yet
  - dryRun: enabled
  - mutation/restore/apply: disabled

## Step 9 (RouteB Investigation RC1)
- RC identity:
  - `rcVersion=RouteB-SceneAwareHistoryPreview-RC1`
  - `routeIdentity=RouteBSceneAwareHistoryPreviewInvestigationRC`
- Completed capabilities:
  - TimelineView detection
  - TimelineViewModel surface inventory
  - runtime fingerprint
  - history matching
  - history preview list
  - sceneAwareMetadata
  - RouteA handoff metadata
  - snapshot pair resolution
  - safe read-only open foundation
- Not included in RC1:
  - RouteA viewer wired open
  - production UI
  - timeline replacement
  - history restore/apply
  - YMM runtime mutation
- Safety:
  - default disabled
  - fallback preserved
  - RouteA preserved
  - production embedding disabled
  - runtime mutation disabled
  - input injection disabled

## Step 9.5 RC Validation Result
- probe:
  - `scene-aware-history-preview-probe-20260513-031341.json`
- summary:
  - `scene-aware-history-preview-summary-20260513-031341.json`
- report:
  - `scene-aware-history-preview-report.md`
- current runtime:
  - `CurrentSceneDetected=True`
  - `SceneHistoryLinkFeasible=True`
  - `Confidence=High`
  - `HistoryPreview.PreviewItemCount=19`
  - `HistoryPreview.HasHighConfidenceMatch=True`
  - `SnapshotPairResolution.Resolved=True`
  - `RouteAOpenReadiness.CanOpen=True`
- RC metadata/readiness blocks:
  - `routeBInvestigationRc`: missing in this measured probe
  - `routeBInvestigationReadiness`: missing in this measured probe
- safety:
  - `DefaultDisabled=True`
  - `FallbackPreserved=True`
  - `RouteAPreserved=True`
  - `ProductionEmbedding=False`
  - `RuntimeMutation=False`
  - `InputInjection=False`
- decision:
  - `runtime revalidation pending` (measured files were generated before latest RC metadata output path)

## Step 10: Preview UI Consolidation Foundation
- Added section-oriented preview layout foundation:
  - Runtime Context
  - Scene-aware History Matches
  - RouteA Handoff / Open Readiness
  - Diagnostics
  - RC / Safety
- Added `previewUiConsolidation` optional output block.
- Scope:
  - default disabled
  - investigation preview only
  - production UI not enabled
  - RouteA viewer not wired

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
