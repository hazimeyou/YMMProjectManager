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
