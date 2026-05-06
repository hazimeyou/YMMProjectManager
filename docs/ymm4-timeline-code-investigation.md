# YMM4-Timeline Code Investigation (preview13)

Target repository: [routersys/YMM4-Timeline](https://github.com/routersys/YMM4-Timeline)
Checked date: 2026-05-06 (JST)

## Confirmed Code Flow (from YMM4-Timeline)

- Entry plugin: `TimelineToolPlugin : IToolPlugin`
- View host: `TimelineToolView` with `YukkuriMovieMaker.Views.TimelineView`
- VM bridge: `TimelineToolViewModel : IToolViewModel, ITimelineToolViewModel, IDisposable`
- Entry method: `SetTimelineToolInfo(TimelineToolInfo info)`
- VM create pattern:
  - `new TimelineViewModel(scene, info.UndoRedoManager, info.AsyncAwaitStatus)`

## Reflection Probe Added in preview13

Added:

- `YmmTimelineReflectionProbe`
- `YmmTimelineReflectionResult`
- `YmmTimelineReflectionLog`

Probe responsibilities:

- Enumerate loaded assemblies
- Search target types (`TimelineView`, `TimelineViewModel`, `UndoRedoManager`, `AsyncAwaitStatus`)
- Enumerate `TimelineViewModel` constructors (public/non-public)
- Search method owner for `SetTimelineToolInfo`
- Produce structured readiness result

## Result Interpretation

- `CanAttemptExperimentalHost = true`
  - type-level prerequisites are present
  - constructor candidates found
  - adapter can transition to `ExperimentalReady`
- `CanAttemptExperimentalHost = false`
  - treated as normal failure
  - fallback path remains active

## Access Modifier / Dependency Notes

- Constructor discovery includes non-public constructors for risk visibility
- Internal/private-only runtime requirements are considered a stop signal for formal integration
- Strong YMM4 runtime coupling remains the primary risk

## Go / No-Go for Generation Step

Proceed to generation trial only when:

- `TimelineView` type found
- `TimelineViewModel` type found
- constructor candidates exist
- required dependency types are available
- dispose path is observable

Do not proceed when:

- probe cannot resolve key runtime types
- dependency chain requires unstable private internals
- fallback safety cannot be preserved
