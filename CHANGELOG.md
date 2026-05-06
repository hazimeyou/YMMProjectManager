# Changelog

## v0.2.9-history-preview14

- Added constructor binding dry-run for `TimelineView` / `TimelineViewModel`
  - `YmmTimelineConstructorBinder`
  - `YmmTimelineConstructorBindingResult`
  - `YmmTimelineConstructorParameterResult`
  - `YmmTimelineGenerationReadiness`
  - `YmmTimelineDependencyResolver`
- Added generation readiness score and blocking/warning diagnostics
- Integrated dry-run into experimental host initialization path
- Added timeline binding diagnostics JSON output (`logs/diagnostics/timeline-binding-*.json`)
- Kept experimental mode default-disabled and fallback-safe behavior

## v0.2.9-history-preview13

- Added reflection-based timeline probe:
  - `YmmTimelineReflectionProbe`
  - `YmmTimelineReflectionResult`
  - `YmmTimelineReflectionLog`
- Added `ExperimentalReady` state to `PureTimelineStatus`
- Future adapter now transitions to `ExperimentalReady` when probe prerequisites are satisfied
- Experimental host window now displays structured probe output (assemblies/types/constructors/missing dependencies)
- Added reflection diagnostics metrics and JSON output (`logs/diagnostics`)
- Added generation criteria doc for preview14 decision

## v0.2.9-history-preview12

- Added `ExperimentalYmmTimelineHostWindow` and `ExperimentalYmmTimelineHostViewModel`
- Added `PureTimelineExperimentalOptions` with default-disabled experimental mode
- Added guarded experimental initialize path in `FutureYmmTimelineAdapter`
- Added dispose-safety diagnostics (`initialize/dispose/active host/failure counts`)
- Added benchmark metrics for experimental host initialize/dispose

## v0.2.9-history-preview11

- Added `FutureYmmTimelineAdapter` scaffold
- Added `PureTimelineAdapterKind` and adapter-kind switching base
- Added fallback verification path (future adapter failure to placeholder)

## v0.2.9-history-preview10

- Added pure timeline adapter boundary (`IPureTimelineAdapter`)
- Added `PureTimelineHostViewModel` and `PlaceholderPureTimelineAdapter`

## v0.2.9-history-preview9

- Timeline sync UX improvements
- CurrentFrame navigation and frame-based jumps

## v0.2.9-history-preview8

- Pure timeline investigation docs
- Timeline sync PoC

## v0.2.9-history-preview7

- Timeline UX improvements (zoom/ruler/grouping)

