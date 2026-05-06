# History Preview Roadmap

## Positioning

`v0.2.9-history-preview` is an experimental track for architecture validation before formal v0.3.0.

## Priorities

1. Foundation stability
2. Semantic diff accuracy
3. Gradual PureTL + DiffTL architecture

## Timeline Policy

- PureTL side: reference YMM4-Timeline patterns (SetTimelineToolInfo, scene rebind, dispose)
- DiffTL side: custom read-only timeline for snapshot diffs

## Investigation Summary

Reusable ideas:

- `SetTimelineToolInfo` entry contract
- `TimelineViewModel(scene, UndoRedoManager, AsyncAwaitStatus)` creation flow
- recreate-before-rebind with explicit dispose

Not reused directly:

- direct plugin embedding model
- direct compile-time dependency for DiffTL

## Release Plan

### v0.3.x

- Stabilize history/diff core and measurements
- Keep UI changes incremental

### v0.4.0

- Evaluate full `PureTL + DiffTL + Detail` composition
- Keep restore/branch/merge out of scope

## Preview8 Additions

- Pure timeline investigation docs
- Timeline sync design doc
- One-way frame sync PoC

## Preview9 Additions

- CurrentFrame navigation
- SyncState/TimelineMode switching
- Pure timeline integration checklist

## Preview10 Additions

- Adapter boundary (`IPureTimelineAdapter`)
- Placeholder host model
- Fallback design doc

## Preview11 Additions

- `FutureYmmTimelineAdapter` scaffold
- Adapter kind switching base
- Future-failure fallback verification

## Preview12 Additions

- Experimental isolated host PoC for TimelineView probing
- Guarded future adapter initialization (default disabled)
- Dispose safety diagnostics and active host counters
- Criteria documentation for go/no-go full integration

## Preview13 Candidates

- Optional runtime bridge for one-way frame sync from PureTL probe
- Reinitialize test harness for repeated scene switch simulation
