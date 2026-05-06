# Pure Timeline Fallback Design (preview13)

## Goal

Keep DiffTimeline usable even when pure timeline probing/initialization fails.

## Mandatory Behavior

- `ProjectDiffWindow` does not close on probe failure
- DiffTL standalone continues to work
- Error is surfaced in host status (`LastError`)
- Placeholder fallback can be activated

## Reflection Failure Policy

- Missing type/member is treated as normal failure
- Status may remain `Unavailable` (not fatal)
- Exceptions are captured and logged

## ExperimentalReady Policy

- Enter `ExperimentalReady` only when probe prerequisites are satisfied
- `ExperimentalReady` does not imply formal integration
- Actual generation remains out-of-scope in preview13

## Diagnostics

Track at minimum:

- `timelineReflectionProbeMs`
- `timelineReflectionAssemblyCount`
- `timelineReflectionTypeFoundCount`
- `timelineReflectionFailureCount`
- `experimentalReadyCount`

## preview15 Generation Attempt Fallback

- Even when generation gate passes, failure of generation or dispose must not close Diff window.
- Generation attempt failure keeps adapter unavailable and allows fallback/standalone continuity.
- `TimelineView` remains ungenerated in preview15 to reduce integration risk.
