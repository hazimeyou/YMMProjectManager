# DiffTimeline Standalone Pipeline

## Current Stage

- Shadow validation: ready
- Manual opt-in standalone route: ready (guarded)
- Limited opt-in release validation: ready (guarded + rollback guard + history trend)
- Default: disabled
- Legacy fallback: always preserved
- TimelineView integration: frozen

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
