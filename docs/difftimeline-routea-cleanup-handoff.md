# DiffTimeline RouteA Cleanup Handoff

## Current Status
- RouteA adopts standalone DiffTimeline for Preview Workspace flow.
- RC integrity artifacts remain active (including `rc-validation-summary.json`).
- UI/Timeline visual cleanup is complete (compact diagnostics + timeline-first layout kept).
- `ProjectDiffViewModel` has been split into partial files for responsibility clarity.
- TimelinePresentation helpers/builders/resolvers are separated by domain.
- Metrics source has been consolidated through `DiffTimelineMetricsSnapshot`.
- Metrics consistency guard is active in diagnostics details.
- Metrics refresh flow and workspace state refresh flow are consolidated.

## Key Commits (Chronological)
- `c586d0b` `chore(timeline): checkpoint before aggressive cleanup`
- `c951291` `refactor(timeline): reduce duplicate diagnostics notifications`
- `50d04d7` `refactor(timeline): split and consolidate ProjectDiffViewModel responsibilities`
- `2394ddd` `docs(timeline): add diagnostics export mapping reference`
- `9d5f546` `refactor(timeline): add metrics snapshot consistency guard`
- `8b4c170` `refactor(timeline): consolidate workspace state refresh flow`

## Responsibility Structure
### ViewModel Partial Files
- `YMMProjectManager/Presentation/ViewModels/ProjectDiffViewModel.cs`
- `YMMProjectManager/Presentation/ViewModels/ProjectDiffViewModel.Compare.cs`
- `YMMProjectManager/Presentation/ViewModels/ProjectDiffViewModel.Sessions.cs`
- `YMMProjectManager/Presentation/ViewModels/ProjectDiffViewModel.RowWindow.cs`
- `YMMProjectManager/Presentation/ViewModels/ProjectDiffViewModel.Notifications.cs`

### TimelinePresentation Domains
- `YMMProjectManager/Presentation/TimelinePresentation/Display`
- `YMMProjectManager/Presentation/TimelinePresentation/Diagnostics`
- `YMMProjectManager/Presentation/TimelinePresentation/State`

### Core Helpers/Builders/Resolvers
- `DiffTimelineClipDisplayResolver`
- `DiffTimelineCompareResultReflector`
- `DiffTimelineDiagnosticsTextBuilder`
- `DiffTimelineRowWindowController`
- `DiffTimelinePreviewWorkspaceStateBuilder`
- `DiffTimelineMetricsSnapshot`
- `DiffTimelineMetricsSnapshotBuilder`

## Current Metrics and Workspace State Flow
1. `ProjectDiffViewModel` gathers runtime values.
2. `BuildMetricsSnapshot()` builds a single metrics source.
3. `DiffTimelineMetricsSnapshotBuilder` materializes the metrics snapshot.
4. `BuildCurrentPreviewWorkspaceState()` builds latest workspace state just before validation/export paths.
5. `DiffTimelinePreviewWorkspaceStateBuilder` materializes `DiffTimelinePreviewWorkspaceState`.
6. Validation/export consumers receive state through `DiffTimelinePreviewValidationRunner` and diagnostics export pipeline.

### Refresh Trigger Policy
- `NotifyMetricsRefreshCompleted(bool includeFilterState)` is the unified post-refresh trigger for compare/filter-grouping related updates.
- This centralizes diagnostics + row-window related notification timing and reduces duplicate notification sets.

## Preserved Behavior and Contracts
- Compare flow preserved.
- Snapshot Browser preserved.
- Session save/load preserved.
- Validation logging preserved.
- Diagnostics/export preserved.
- `rc-validation-summary` preserved.
- Large Result Mode + Load More Rows + Reset Row Window preserved.
- UI visual/timeline style preserved.
- JSON output schemas preserved.

## Frozen Scope (Do Not Touch)
- TimelineView integration
- runtime bridge
- ProjectDiffWindow embedding
- timeline replacement
- production embedding

### Prohibited
- production embedding
- timeline replacement
- runtime mutation
- input injection
- command execution
- unsafe integration

## Open Risks / Remaining Work
- Codebase size is still large (~20k lines), but now responsibility-separated.
- Experimental/frozen areas remain intentionally untouched (`D` class candidates).
- diagnostics/export duplication mapping is documented; next steps should be incremental and regression-safe.

## Next-Phase Options
1. Code Reduction Phase
- Remove safe unused private methods/fields, reduce duplicate formatting/notification fragments, continue light XAML dedup.

2. Export Integrity Regression Check
- Add stronger regression checks across `preview-workspace-state`, `rc-validation-summary`, and manifest package consistency.

3. Group-aware Large Result Mode (feature-leaning)
- Consider only after cleanup stabilizes.

4. Frozen/Experimental Inventory
- Inventory-only pass; no deletion.

## Operational Notes
- Primary diagnostics/export mapping reference:
  - `docs/difftimeline-diagnostics-export-map.md`
- Handoff principle:
  - Keep behavior stable, keep schemas stable, keep frozen scope untouched, and continue cleanup with reversible small phases.
