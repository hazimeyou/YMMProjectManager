# Pure Timeline Investigation (preview8)

## Target

- Repository: https://github.com/routersys/YMM4-Timeline
- Goal: determine how to prepare `Pure Timeline + Diff Timeline` architecture without hard coupling.

## Reusable Knowledge

- `TimelineView` embedding pattern in plugin UI.
- `SetTimelineToolInfo` flow to resolve current scene/timeline.
- `TimelineViewModel(scene, UndoRedoManager, AsyncAwaitStatus)` construction pattern.
- scene switch lifecycle: detach old event handlers, dispose old VM, recreate VM.
- `Dispose` discipline around timeline/property change subscriptions.

## Intentionally Not Reused As-Is

- Direct plugin dependency on YMM4-Timeline.
- Full source copy of timeline plugin behavior.
- Using YMM4-Timeline as Diff timeline.

## Dependency Risks

- YMM core version drift can break direct TimelineView coupling.
- plugin-level dependencies are broad; lock-in risk is high.
- internal TimelineView behavior may change without API stability guarantees.

## YMMProjectManager Policy

- `Pure Timeline`: use investigation as reference implementation, bring only minimal required patterns.
- `Diff Timeline`: continue custom implementation.
- Keep pure timeline integration optional and resilient to unavailable dependencies.

## Current Preview8 Status

- Added `Pure Timeline Placeholder` panel in `ProjectDiffWindow`.
- Added sync-ready state (`Synced/Detached/Unavailable`) and frame bridge PoC.
- No hard dependency on YMM4-Timeline added.
