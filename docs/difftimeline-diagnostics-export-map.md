# DiffTimeline Diagnostics/Export Mapping (RouteA)

## Background
- RouteA standalone pipeline is adopted for Preview Workspace RC.
- diagnostics/export is core infrastructure for validation and audit.
- RC evidence and export integrity must be preserved during cleanup/reduction.
- This document fixes the mapping before deeper consolidation.

## Output Files
| Output file | Primary generator | Primary purpose | Deletion |
|---|---|---|---|
| `rc-validation-summary.json` | RC validation summary writer / runner | RC identity and integrity snapshot | Forbidden |
| `preview-workspace-state.json` | `ProjectDiffViewModel` + `DiffTimelinePreviewWorkspaceStateBuilder` | Workspace reproducibility and diagnostics state | Forbidden |
| `snapshot-browser-state.json` | Snapshot browser state export path | Snapshot/compare context persistence | Forbidden |
| `comparison-history.json` | `DiffTimelineComparisonHistoryStore` | Compare event audit trail | Forbidden |
| `manual-ui-validation-log.json` | Manual validation log writer | UI action-level validation trace | Forbidden |
| `manual-ui-validation-summary.json` | Manual validation summary writer | Aggregated validation counters and status | Forbidden |
| `manifest.json` | Diagnostics export package writer | Package composition/integrity | Forbidden |
| `preview-readiness-report.json` | Preview validation runner/report pipeline | Preview readiness judgement | Keep |
| `route-validation-report.json` | Route validation/gate report pipeline | RouteA gate evidence | Keep |
| `validation-dashboard.json` | Validation dashboard builder | Human-readable validation overview | Keep |
| `validation-history.json` | Validation history writer | Trend/regression baseline | Keep |

## Semantic Field Mapping
| Field | Appears in | Why duplicated | Consolidation |
|---|---|---|---|
| `rcVersion` | RC summary, release docs, package/report metadata | Identity must exist in multiple audit surfaces | D (keep) |
| `routeIdentity` | RC summary, route report, docs | Route traceability across reports | C/D (value source can be unified) |
| `defaultDisabled` | RC summary, route/report metadata | Policy evidence | D (keep) |
| `fallbackPreserved` | RC summary, route/report metadata | Safety policy evidence | D (keep) |
| `timelineViewIntegrationFrozen` | RC summary/docs/reports | Frozen-scope evidence | D (keep) |
| `runtimeBridgeFrozen` | RC summary/docs/reports | Frozen-scope evidence | D (keep) |
| `LargeResultMode` | Workspace state, UI-facing diagnostics, export state | Runtime state + export reproducibility | B/C (unify source only) |
| `ProjectionCacheStats` | Workspace state/export + diagnostics detail path | Performance traceability | B/C (unify source only) |
| `RenderMetrics` | Workspace state/export + diagnostics detail path | Performance traceability | B/C (unify source only) |
| `HeavyProjectDiagnostics` | Workspace state/export + UI diagnostics | Runtime warning + audit context | B/C (unify source only) |
| `ValidationLog` | Manual validation log/summary | UI validation evidence | D (keep separate) |
| `CompareHistory` | Comparison history file/store | Compare event evidence | D (keep separate) |

## Responsibility Boundaries
### UI diagnostics
- Example: `DiffTimelineDiagnosticsTextBuilder`
- Purpose: immediate readability, compact/detail text for operator UX.

### Export diagnostics
- Purpose: structured JSON for reproducibility and package-level integrity.

### Validation/report
- Purpose: RouteA readiness/gate decisions, trend/history tracking.

### RC evidence
- Purpose: RC identity, frozen-policy proof, release traceability.

## Consolidation Policy
### Allowed
- Unify computation source of shared values (`RenderMetrics`, `ProjectionCacheStats`, `HeavyProjectDiagnostics`, `LargeResultMode`).
- Normalize naming/enums where backward compatibility is preserved.

### Not allowed
- Merge RC summary and manifest into one artifact.
- Merge UI diagnostics text and export JSON schema.
- Merge `ValidationLog` and `CompareHistory` (different audit granularity).

## Deletion-Prohibited List
- RC metadata and `rc-validation-summary` artifacts.
- diagnostics/export foundation and manifest sections.
- validation logging and comparison history.
- fallback path and default-disabled path evidence.
- TimelineView frozen code and runtime bridge frozen code.
- Large Result Mode, row window controls.

## Next Cleanup Direction
1. Phase 1: unify calculation sources only (no schema changes).
2. Phase 2: naming normalization with compatibility checks.
3. Phase 3: add/export integrity regression checks.
4. Phase 4: integrate only A/B candidates incrementally.

## MetricsSnapshot Consistency Guard
- `DiffTimelineMetricsSnapshot` is treated as the shared metrics source for UI diagnostics, preview workspace state, and export diagnostics.
- `ProjectDiffViewModel` appends `MetricsSnapshotConsistencyWarnings=...` in diagnostics details to detect lightweight consistency issues during cleanup/refactor.
- The guard is intended for regression detection and does not change UI/export schemas.

### Current Warning Rules
- `DisplayedRowCount > TotalAvailableRowCount`
- `DeferredRowCount < 0`
- `CanLoadMoreRows == true && DeferredRowCount == 0`
- `IsLargeResultMode == false && DeferredRowCount > 0`
- `TotalAvailableRowCount > 0 && ProjectionCacheStats == null`
