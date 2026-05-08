# DiffTimeline Standalone Pipeline

## Current Stage

- Shadow validation: ready
- Manual opt-in standalone route: ready (guarded)
- Default: disabled
- Legacy fallback: always preserved
- TimelineView integration: frozen

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

## Manual Opt-in Usage

1. Keep default behavior (legacy route): no route flag.
2. Request standalone route:
   - `YMM_STANDALONE_DIFFTIMELINE_ROUTE=1`
3. Optional shadow diagnostics:
   - `YMM_STANDALONE_SHADOW_VALIDATION=1`
4. If gate is blocked, route auto-falls back to legacy.

## Formal Adoption Checklist

1. No blockers across recent runs
2. Comparer confidence meets threshold consistently
3. Missing row threshold satisfied
4. Diagnostics completeness stable
5. Regression detector reports no critical regression
6. Consecutive success count reaches target window

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
