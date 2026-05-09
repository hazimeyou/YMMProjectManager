# DiffTimeline Standalone Pipeline

## Current Stage

- Shadow validation: ready
- Manual opt-in standalone route: ready (guarded)
- Limited opt-in release validation: ready (guarded + rollback guard + history trend)
- Default: disabled
- Legacy fallback: always preserved
- TimelineView integration: frozen
- Preview UX/history foundation: in progress (filter/search/grouping + snapshot browser base)

## Preview UX Direction (RouteA)

- Filtering/search:
  - path filter
  - semantic category filter
  - change type filter
  - text search
  - changed-only / warning-only
  - group filter
- Grouping UX:
  - semantic / timeline / layer / field / path / change-type grouping
  - group metadata (collapsed-ready, row count, semantic/severity summary)
- Row UX metadata:
  - compact-ready
  - icon/highlight/navigation keys
  - sticky-group-ready marker

## ViewModel Wiring Status

- `ProjectDiffViewModel` now keeps preview filter/search state:
  - search text
  - changed-only / warning-only
  - change-type / semantic / path / group filters
- Filtering uses Core pipeline (`DiffTimelineFilterSearchPipeline`) rather than ad-hoc UI logic.
- Grouping mode state is wired:
  - `None`, `Semantic`, `Timeline`, `Layer`, `Field`, `Path`, `ChangeType`
- Group collapse/expand foundation is available via state mutation helpers.
- Snapshot browser foundation ViewModel added:
  - `DiffTimelineSnapshotBrowserViewModel`
  - snapshot list, comparison candidates, selected old/new, compare request generation

## ProjectDiffWindow Preview Controls

- Added minimal preview controls (collapsed by default):
  - search box
  - changed-only / warning-only toggles
  - change type / semantic filters
  - path/group filter text inputs
  - clear filters
  - grouping mode selector
  - expand/collapse all group actions
- Added filter diagnostics display:
  - matched count
  - filtered-out count
  - filter duration
  - active filter summary
- Added snapshot browser preview panel:
  - latest validation state
  - snapshot count
  - comparison candidate count
  - snapshot list (name/created/source/hash/note)
  - old/new snapshot selection
  - swap/clear selection
  - compare request summary and empty state message
  - manual `Compare Selected Snapshots` action (preview-only)
  - compare status/result/error/diagnostics path display
  - compare button is disabled while compare cannot run (missing selection/same snapshot/running)

Current limitation:
- The panel is intentionally minimal and preview-focused.
- Standalone route default is still disabled.
- Legacy fallback remains primary when gate/rollback guard is NG.

### Interaction Validation Notes

- Search input updates filter pipeline state immediately.
- `changed-only` / `warning-only` toggles are wired to Core filter state.
- ChangeType/Semantic combo filters are bound through string option lists (no ad-hoc view filter logic).
- Path/Group text filters feed Core filter pipeline directly.
- `Clear Filters` resets all preview filter states.
- Grouping mode switch is connected (`None/Semantic/Timeline/Layer/Field/Path/ChangeType`).
- Expand/Collapse all actions are safe state toggles and do not mutate core data.
- Filter diagnostics display:
  - matched count
  - filtered-out count
  - filter duration
  - active filter summary
- No-match state text is shown when filtered row count becomes zero.
- Diagnostics export now includes preview UI state payloads when available:
  - filter/search state
  - snapshot browser state
  - comparison history
  - selected old/new snapshot hashes
  - compare summary text and compare availability context
  - latest compare execution status/error summary

### Snapshot Compare Preview Notes

- This compare flow is preview/manual only.
- Default route remains disabled; legacy fallback remains primary.
- Typical blocked/no-op conditions:
  - old/new snapshots not selected
  - same snapshot selected for old/new
  - snapshot body missing in repository
  - pipeline failure/fallback reason returned

## Manual UI Validation Logging

- Manual preview interactions now emit validation logs under `diagnostics/`:
  - `manual-ui-validation-<sessionId>.json`
  - `manual-ui-validation-summary-<sessionId>.json`
- Action types include:
  - `SnapshotSelected`, `SnapshotSwapped`, `SnapshotCleared`
  - `CompareStarted`, `CompareSucceeded`, `CompareFailed`, `CompareBlocked`, `CompareNoOp`
- Logs are preview/internal diagnostics for traceability and do not change default route behavior.

## Snapshot Persistence / Reusable Compare Session (Preview)

- Reusable compare sessions are stored in diagnostics:
  - `difftimeline-reusable-compare-sessions.json`
- Snapshot persistence metadata foundation:
  - `difftimeline-persisted-snapshots.json`
- Session stores:
  - old/new snapshot hashes
  - compare options
  - filter/search/grouping state
  - latest diagnostics/log paths
- ProjectDiffWindow preview panel now supports:
  - save current compare session
  - restore selected compare session (restore-only)

## Snapshot/History Foundation

- Snapshot repository:
  - named snapshot
  - created timestamp
  - source project
  - note/tag
  - hash index
- Retention planning:
  - keep-latest target
  - cleanup candidate identification (diagnostics-only)
- Comparison history:
  - old/new snapshot hash
  - compare timestamp
  - summary and metadata
- Snapshot browser foundation:
  - list item model
  - comparison candidate model
  - snapshot detail summary
  - latest validation state

## RouteA Roadmap (Preview UX / History Expansion)

1. Stabilize filter/search defaults on large row sets.
2. Add persisted filter presets and compare-session recall.
3. Connect snapshot browser foundation to isolated preview ViewModel.
4. Expand diagnostics export for filter/search/history diff sessions.
5. Keep standalone route default disabled until trend-based readiness is sustained.

## Environment Flags (Safe Defaults)

- `YMM_STANDALONE_SHADOW_VALIDATION` (`0`/`1`, default `0`)
- `YMM_STANDALONE_DIFFTIMELINE_ROUTE` (`0`/`1`, default `0`)
- `YMM_STANDALONE_DIAGNOSTICS_VERBOSITY` (default `standard`)
- `YMM_STANDALONE_HISTORY_KEEP_COUNT` (default `50`)
- `YMM_STANDALONE_GATE_POLICY_OVERRIDE` (`0`/`1`, default `0`)
- `YMM_STANDALONE_CONSECUTIVE_FAILURE_THRESHOLD` (default `2`)
- `YMM_STANDALONE_STRICT_REGRESSION_BLOCKING` (`0`/`1`, default `1`)
- `YMM_STANDALONE_STRICT_DIAGNOSTICS_COMPLETENESS` (`0`/`1`, default `1`)
- `YMM_STANDALONE_STRICT_CACHE_ANOMALY_BLOCKING` (`0`/`1`, default `1`)

## Validation Run History

Standalone validation now records run history entries containing:

- timestamp
- project identity
- old/new snapshot hash
- requested/selected route
- gate result
- comparer confidence
- blockers/warnings
- cache hit/miss
- diagnostics path
- recommendation/fallback reason

History is saved under `diagnostics/difftimeline-validation-run-history.json`.

## Regression Detector

Regression detection compares latest run with previous run and flags:

- confidence drop
- blocker increase
- fallback increase
- cache behavior anomaly
- diagnostics incomplete

Trend readiness adds:

- stable run count
- consecutive success count
- latest regression summary
- promotion recommendation

## Rollback Guard

Even when route gate is technically passable, standalone route is rejected and legacy fallback is forced when:

- consecutive failures reach threshold
- regression is detected (strict mode)
- diagnostics are incomplete
- cache/hash anomaly is detected

This is evaluated before selecting standalone route in manual opt-in mode.

## Validation Dashboard + Export Package

Validation now builds a dashboard model with:

- latest run timestamp
- selected/requested route
- gate result + reason
- trend readiness + regression summary
- cache status
- blockers/warnings
- final recommendation

Diagnostics export package bundles:

- latest diagnostics JSON
- route validation report
- validation history
- dashboard snapshot
- standalone config snapshot
- manifest
- preview readiness report
- preview package manifest (`preview-package-manifest.json`)

## Preview Package (v1)

Preview package export includes:

- `preview-package-manifest.json`
- `preview-readiness-report.json`
- `validation-dashboard.json`
- `validation-history.json`
- `route-validation-report.json`
- latest standalone pipeline diagnostics JSON (if present)
- `manifest.json`

Manifest contains:

- version
- commit hash
- required env flags
- default-disabled status
- fallback preserved status
- readiness report path
- diagnostics export path
- known limitations

## Preview Release Checklist (Final)

1. Build passes with `0 warning / 0 error`.
2. Default-disabled policy remains true.
3. Legacy fallback path remains selectable and healthy.
4. Promotion gate and rollback guard both evaluated.
5. Validation history updated and regression check executed.
6. Preview readiness report generated.
7. Preview package manifest generated.
8. Export package includes:
   - `manifest.json`
   - `preview-package-manifest.json`
   - `preview-readiness-report.json`
   - `validation-dashboard.json`
   - `validation-history.json`
   - `route-validation-report.json`
9. Known limitations reviewed and accepted for preview scope.
10. TimelineView integration remains frozen.

## v1 Preview Validation Procedure

1. Confirm default-safe baseline:
   - `YMM_STANDALONE_DIFFTIMELINE_ROUTE` is not set (or `0`)
   - `YMM_STANDALONE_SHADOW_VALIDATION=1`
2. Run validation and confirm diagnostics export package is generated.
3. Inspect:
   - `route-validation-report.json`
   - `validation-dashboard.json`
   - `preview-readiness-report.json`
   - `validation-history.json`
4. Enable manual route only for verifier sessions:
   - `YMM_STANDALONE_DIFFTIMELINE_ROUTE=1`
5. If promotion gate or rollback guard blocks, route must remain legacy.
6. Collect multiple stable runs before any broader rollout decision.
7. Share preview package directory with verifier.

## Required Flags for Preview

- Required for route trial:
  - `YMM_STANDALONE_DIFFTIMELINE_ROUTE=1`
- Strongly recommended during preview:
  - `YMM_STANDALONE_SHADOW_VALIDATION=1`
  - `YMM_STANDALONE_DIAGNOSTICS_VERBOSITY=standard` (or stricter)

## Known Limitations

- Standalone route remains opt-in and non-default.
- Diagnostics quality is a hard dependency for promotion decisions.
- Regression trend can block preview even when a single run looks healthy.
- TimelineView integration remains frozen and is out of RouteA scope.
- Preview package is validation-only; no default route promotion is performed.

## Disable / Rollback Procedure

1. Unset or set `YMM_STANDALONE_DIFFTIMELINE_ROUTE=0`.
2. Keep `YMM_STANDALONE_SHADOW_VALIDATION=1` only if diagnostics collection is needed.
3. If rollback guard reports blockers, use legacy route only.
4. Preserve diagnostics package for postmortem.
5. Record rollback reason from preview runner `FailureReasons`.

## Prohibited Actions During Preview

- Enable standalone route by default
- Re-enable TimelineView integration/runtime bridge
- Remove legacy fallback path
- Treat preview output as production-ready without gate/trend checks

## Manual Opt-in Usage

1. Keep default behavior (legacy route): no route flag.
2. Request standalone route:
   - `YMM_STANDALONE_DIFFTIMELINE_ROUTE=1`
3. Optional shadow diagnostics:
   - `YMM_STANDALONE_SHADOW_VALIDATION=1`
4. If gate is blocked, route auto-falls back to legacy.
5. If rollback guard fails, route auto-falls back to legacy even when gate is nominal.

## Formal Adoption Checklist

1. No blockers across recent runs
2. Comparer confidence meets threshold consistently
3. Missing row threshold satisfied
4. Diagnostics completeness stable
5. Regression detector reports no critical regression
6. Consecutive success count reaches target window
7. Rollback guard remains pass across recent runs

## Rollback Conditions

Rollback to legacy route is mandatory when:

- promotion gate blocked
- confidence below threshold
- missing rows beyond threshold
- diagnostics incomplete
- pipeline envelope failure/exception
- regression detector reports blocker increase

## Safety Boundaries

- No Experimental reactivation
- No TimelineView integration restart
- No runtime bridge revival
- No WPF types in Core
- No production embedding/timeline replacement
- PlaceholderAdapter and legacy fallback preserved

## Default-Disabled Policy

- `EnableExperimentalYmmTimelineHost=false`
- `AllowViewModelGenerationAttempt=false`
- standalone route flags are opt-in only
- fallback route remains legacy when any guard fails

## Changelog Draft (v1 Preview)

- Added preview readiness checker and report model.
- Added diagnostics export package with preview artifacts.
- Added rollback guard integration with validation history trend.
- Added preview validation runner (self-check + readiness + export orchestration).
- Added preview package manifest with version/commit/env/fallback metadata.
- Expanded self-check to cover package readiness assertions.
- Expanded docs for verifier workflow, rollback, and limits.

## Real Project Validation Notes

- Execution mode:
  - `YMM_STANDALONE_SHADOW_VALIDATION=1`
  - Optional manual route trial: `YMM_STANDALONE_DIFFTIMELINE_ROUTE=1`
- Real input source priority:
  1. explicit parameters
  2. env paths:
     - `YMM_STANDALONE_VALIDATION_OLD_PATH`
     - `YMM_STANDALONE_VALIDATION_NEW_PATH`
  3. benchmark fixtures fallback (`modified-text/before.ymmp`, `after.ymmp`)
- Diagnostics package paths:
  - `diagnostics/difftimeline-export-*/preview-package-manifest.json`
  - `diagnostics/difftimeline-export-*/preview-readiness-report.json`
  - `diagnostics/difftimeline-export-*/validation-history.json`
  - `diagnostics/difftimeline-export-*/validation-dashboard.json`
  - `diagnostics/difftimeline-export-*/route-validation-report.json`
- Review focus:
  - `FailureReasons`
  - promotion gate blockers/warnings
  - rollback guard reason
  - regression warnings
  - snapshot hash and pipeline hash metadata

### Validation Memo (2026-05-09)

- Execution time:
  - 2026-05-09 (JST)
- Input route:
  - env real-project path
  - `YMM_STANDALONE_VALIDATION_OLD_PATH=C:\Users\yu-za-hazimeyou\Desktop\YukkuriMovieMaker_v4_Lite\user\backup\2026-05-07 01-57-48-6 差分チェック用.ymmp`
  - `YMM_STANDALONE_VALIDATION_NEW_PATH=C:\Users\yu-za-hazimeyou\Desktop\YukkuriMovieMaker_v4_Lite\user\backup\2026-05-07 20-04-33-3 無題.ymmp`
- Flags:
  - `YMM_STANDALONE_SHADOW_VALIDATION=1`
  - `YMM_STANDALONE_DIFFTIMELINE_ROUTE` not enabled (default disabled preserved)
- Export package:
  - `diagnostics/difftimeline-export-20260509-075012`
  - included: manifest / preview readiness / dashboard / history / route report / package manifest
- Summary:
  - `Succeeded=true`
  - `FailureReasons=[]`
  - `preview-readiness-report.CanPreview=true`
  - warnings: `trend-not-ready` (expected for early run history)
- Snapshot / hash:
  - `snapshotSource=env-real-project`
  - old/new snapshot hash present
  - `pipelineResultHash` present
- Blocker status:
  - promotion blockers: none
  - rollback guard blockers: none
  - regression critical warnings: none
- Preview release judgement:
  - opt-in preview validation is feasible
  - continue accumulating history for trend readiness before broader rollout
