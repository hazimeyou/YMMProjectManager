# Experimental TimelineView Generation Criteria

## Purpose

Define objective criteria for whether preview14 should attempt actual `TimelineView` generation.

## Conditions to Proceed

- `TimelineView` type resolution succeeded
- `TimelineViewModel` type resolution succeeded
- Constructor candidates are discoverable
- Required dependency types are available (`UndoRedoManager`, `AsyncAwaitStatus`)
- Dispose path is understood and repeatable
- Fallback to placeholder remains guaranteed

## Conditions to Hold

- Critical types missing in reflection probe
- Constructor requires inaccessible/private-only runtime objects
- Scene resolution path cannot be recreated safely
- Dispose safety is uncertain
- YMM runtime coupling risk is too high for current preview
- Any failure can break DiffTL standalone behavior

## Safety Constraints

- Experimental mode stays disabled by default
- Reflection failures are treated as normal failures
- Adapter boundary (`IPureTimelineAdapter`) must absorb all failures
- `ProjectDiffWindow` and DiffTL must remain usable regardless of probe result
