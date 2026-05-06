# Pure Timeline Integration Checklist (preview13)

- TimelineView type can be resolved
- TimelineViewModel type can be resolved
- Constructor signatures can be discovered
- `UndoRedoManager` type can be resolved
- `AsyncAwaitStatus` type can be resolved
- `SetTimelineToolInfo` method owner can be located
- Reflection probe result is persisted for diagnostics
- Experimental mode is disabled by default
- Adapter can enter `ExperimentalReady`
- Reflection failure does not crash host window
- Placeholder fallback remains available
- DiffTL standalone remains usable
- Dispose path remains safe under repeated init/dispose
