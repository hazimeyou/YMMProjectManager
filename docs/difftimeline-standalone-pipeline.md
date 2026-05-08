# DiffTimeline Standalone Pipeline

## Current Stage

- Shadow validation: ready
- Manual opt-in standalone route: ready (guarded)
- Default: disabled
- Legacy fallback: always preserved
- TimelineView integration: frozen

## Manual Opt-in Usage

1. Keep default behavior (legacy route): do not set route env flag.
2. Enable standalone route request (manual only):
   - `YMM_STANDALONE_DIFFTIMELINE_ROUTE=1`
3. Optional shadow validation diagnostics:
   - `YMM_STANDALONE_SHADOW_VALIDATION=1`
4. Route is promoted only if promotion gate allows it.
5. If gate is blocked or pipeline fails, route automatically falls back to legacy.

## Promotion Gate Checklist

Promotion gate checks include:

- comparer confidence threshold (`>= 0.95` default)
- missing row threshold (`<= 0` default)
- extra row threshold (`<= 20` warning threshold)
- row count mismatch tolerance (`<= 20` default)
- diagnostics completeness checks
- fallback reason presence checks
- blocker/warning classification

Formal adoption should require:

1. No blockers in repeated runs
2. Stable diagnostics output
3. High comparer confidence over multiple datasets
4. Consistent cache behavior

## Rollback Conditions

Immediate rollback to legacy route when any of these occurs:

- pipeline envelope failure (`IsSuccess=false`)
- promotion gate blocked
- missing rows beyond threshold
- confidence below threshold
- unexpected exception during route build
- diagnostics required fields missing

Rollback is automatic and keeps existing UI route intact.

## Diagnostics Scope

Diagnostics JSON includes:

- route selection result
- route validation report
- promotion readiness
- comparer summary
- cache hit/miss
- adapter diagnostics
- fallback reason
- environment flag snapshot
- pipeline hashes

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
