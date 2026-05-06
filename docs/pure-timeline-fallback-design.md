# Pure Timeline Fallback Design (preview12)

## Goal

Even if Pure Timeline integration fails, DiffTimeline (DiffTL) must remain usable in standalone mode.

## Must Not Break

- `ProjectDiffWindow` stays open
- DiffTL rendering and selection continue
- CurrentFrame line remains available
- List/Timeline tab usage continues

## Failure Policy

- `InitializeAsync` failure:
  - keep window alive
  - store reason in `LastError`
  - switch to placeholder fallback
- `SetCurrentFrameAsync` / `CenterFrameAsync` failure:
  - isolate error in pure timeline side
  - keep DiffTL navigation active
- `DisposeAsync` failure:
  - never crash app
  - increment dispose-failure diagnostics

## preview12 Additions

- Added isolated experimental host probe:
  - `ExperimentalYmmTimelineHostWindow`
  - `ExperimentalYmmTimelineHostViewModel`
- Added guarded future adapter options:
  - `EnableExperimentalYmmTimelineHost` (default false)
  - `UseReflection`
  - `OpenIsolatedHostWindow`
- Added diagnostics counters:
  - initialize count
  - dispose count
  - dispose failure count
  - active host count
  - experimental host success/failure count

## Runtime Modes

- `Placeholder`: safe default
- `FutureYmmTimeline` disabled path: intentional fail + fallback
- `FutureYmmTimeline` experimental path: isolated probe only (no formal integration)
