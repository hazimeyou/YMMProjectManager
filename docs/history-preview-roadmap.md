# History Preview Roadmap

## Positioning

`v0.2.9-history-preview` is an experimental track before formal `v0.3.0`.

## Core Priorities

1. Foundation stability
2. Semantic diff accuracy
3. PureTL + DiffTL staged architecture

## Timeline Direction

- PureTL side: investigate and isolate YMM timeline access
- DiffTL side: keep custom read-only diff timeline

## Preview Progress

### preview8

- Pure timeline investigation docs
- Timeline sync design
- One-way frame sync PoC

### preview9

- CurrentFrame navigation
- SyncState/TimelineMode switching

### preview10

- Adapter boundary (`IPureTimelineAdapter`)
- Placeholder host model
- Fallback design

### preview11

- Future adapter scaffold
- Adapter-kind switching base
- Failure-to-fallback verification

### preview12

- Isolated experimental host window
- Guarded initialize path (default disabled)
- Dispose safety diagnostics

### preview13

- Reflection-based timeline access probe
- Structured probe result model
- `ExperimentalReady` state transition
- Reflection diagnostics output (`logs/diagnostics`)
- Generation criteria doc for preview14 decision

### preview14

- Constructor binding dry-run for `TimelineView` / `TimelineViewModel`
- Parameter-level dependency resolvability diagnostics
- Generation readiness scoring (`0-100`) with blocking reasons
- Dry-run integration into `FutureYmmTimelineAdapter` experimental path
- Diagnostics JSON output: `logs/diagnostics/timeline-binding-*.json`

### preview15

- Isolated TimelineViewModel generation attempt gate added
- `AllowViewModelGenerationAttempt` default-disabled safety switch
- Readiness threshold option (`MinimumReadinessScoreForGeneration=80`)
- Immediate dispose verification after successful generation
- Generation attempt diagnostics JSON output (`logs/diagnostics/timeline-generation-attempt-*.json`)

## preview16 Candidates

- Optional isolated `TimelineView` generation attempt (still no formal embed)
- Broader constructor value resolver experiments (still runtime-safe)
- Repeated generation/dispose stress diagnostics

### preview16

- RuntimeEnvironmentDetector added (`Benchmark` / `YMM4Plugin` / `Standalone` / `Unknown`)
- Manual probe actions added to experimental host UI
- Diagnostics filenames now include runtime kind
- Runtime metadata included in diagnostics JSON

## preview17 Candidates

- Validate probe results from real YMM4 plugin runtime session
- Evaluate guarded next-step for ViewModel generation only in YMM4 runtime

### preview18

- Added explicit user-action path for isolated TimelineViewModel generation attempt
- Added immediate dispose verification with post-dispose GC reachability note
- Extended generation diagnostics (null-injected parameters, stack trace)
- Maintained fallback-first behavior and default-disabled experimental execution

### preview19

- Added strict dependency resolution gate for isolated generation attempt
- Scene / UndoRedoManager / AsyncAwaitStatus are handled as required YMM runtime dependencies
- Null injection is no longer treated as generation-safe for required YMM runtime dependencies
- Generation is skipped when required YMM runtime dependencies are unresolved (with diagnostics reason)

### preview20

- Added runtime dependency instance discovery phase diagnostics
- Added safe Application.Current.Windows / DataContext graph traversal with depth limits
- Added owner/property/field/method route candidates for Scene, UndoRedoManager, AsyncAwaitStatus
- Added dependency discovery summaries for feasibility analysis
