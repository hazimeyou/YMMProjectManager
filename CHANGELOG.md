# Changelog

## v0.2.9-history-preview19

- Added strict dependency resolution gate for isolated generation attempts
- Treated Scene, UndoRedoManager, and AsyncAwaitStatus as RequiredYmmRuntimeDependency
- Stopped treating nullable YMM runtime dependencies as resolvable by null injection
- Generation attempt now skips when required YMM runtime dependencies are unresolved
- Kept diagnostics skip-reason output and fallback-safe behavior
## v0.2.9-history-preview18

- Added explicit user-action button for isolated TimelineViewModel generation attempt (生成試行(即破棄))
- Kept generation default-disabled in normal flow; no automatic generation added
- Extended generation diagnostics with:
  - constructor parameter dump
  - null-injected parameter list
  - exception stack trace
  - immediate dispose result
  - post-dispose GC reachability note
- Preserved fallback-safe behavior (DiffTimeline standalone and placeholder path)
## v0.2.9-history-preview16

- Added runtime environment detection (`Benchmark` / `YMM4Plugin` / `Standalone` / `Unknown`)
- Added manual runtime probe actions in experimental host UI
- Added runtime-aware diagnostics output names:
  - `timeline-probe-<runtime>-*.json`
  - `timeline-binding-<runtime>-*.json`
- Added runtime metadata to diagnostics JSON (`runtimeKind`, `processName`, `ymmAssemblyNames`)
- Added runtime context messaging in future adapter initialization path
- Kept generation attempt default-disabled and fallback-safe behavior

## v0.2.9-history-preview15

- Added isolated TimelineViewModel generation-attempt gate in experimental host
- Added `AllowViewModelGenerationAttempt` (default `false`)
- Added readiness threshold option (`MinimumReadinessScoreForGeneration`, default `80`)
- Added immediate dispose verification flow for generation attempts
- Added generation attempt result model and diagnostics output (`logs/diagnostics/timeline-generation-attempt-*.json`)
- Added generation/dispose diagnostics counters for timeline experimentation
- Kept TimelineView generation out-of-scope and maintained fallback-safe behavior

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





