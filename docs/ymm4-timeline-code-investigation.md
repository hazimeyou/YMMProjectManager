# YMM4-Timeline Code Investigation (preview12)

Target repository: [routersys/YMM4-Timeline](https://github.com/routersys/YMM4-Timeline)
Checked date: 2026-05-06 (JST)

## Confirmed Classes and Flow

- Plugin entry: `Timeline/TimelineToolPlugin.cs`
  - Implements `IToolPlugin`
  - Publishes `ViewModelType` and `ViewType`
- View host: `Timeline/TimelineToolView.xaml`
  - Hosts `YukkuriMovieMaker.Views.TimelineView`
- ViewModel bridge: `Timeline/TimelineToolViewModel.cs`
  - Implements `IToolViewModel`, `ITimelineToolViewModel`, `IDisposable`

## Timeline Creation Path

- Entry method: `SetTimelineToolInfo(TimelineToolInfo info)`
- Scene resolution:
  - `info.Scenes.AllScenes.FirstOrDefault(s => s.Timeline == info.Timeline)`
- ViewModel creation:
  - `new TimelineViewModel(scene, info.UndoRedoManager, info.AsyncAwaitStatus)`

## Scene Switch / Dispose Handling

- Recreate path calls `DisposeTimelineViewModel()` before rebuilding
- `Timeline` property changed event is unsubscribed on dispose
- `TimelineViewModel.Dispose()` is called explicitly

## Multi-Panel Behavior

- `AllowMultipleInstances => true`
- Uses panel id tracking (`usedIds`)
- Creates additional tool views via `CreateNewToolViewRequested`

## Dependency Findings

`Timeline.csproj` references many YMM4 runtime assemblies:

- `YukkuriMovieMaker.dll`
- `YukkuriMovieMaker.Controls.dll`
- `YukkuriMovieMaker.Plugin.dll`
- `AvalonDock`, `NAudio`, `Vortice.*`, `SharpGen.*` and others

This confirms strong version-coupling risk for direct embed.

## preview12 PoC Result (Isolated Host)

- Reflection-based probing added in `ExperimentalYmmTimelineHostViewModel`
- TimelineView type resolution and instance creation can be attempted in isolation
- TimelineViewModel constructor signatures are enumerated for dependency mapping
- The adapter remains guarded by default:
  - `EnableExperimentalYmmTimelineHost = false`

## Integration Judgment

Proceed only when:

- Required runtime objects (`scene`, `UndoRedoManager`, `AsyncAwaitStatus`) are resolvable safely
- Dispose/reinitialize path is repeatable without leaks
- Failure path keeps DiffTL standalone and window alive

Do not proceed when:

- Any hard crash occurs on initialize/dispose
- Runtime type binding depends on unstable private internals
- Optional adapter boundary cannot absorb YMM version drift
