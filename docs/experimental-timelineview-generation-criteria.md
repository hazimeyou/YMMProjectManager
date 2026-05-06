# Experimental TimelineView Generation Criteria

## Purpose

Define objective criteria for whether preview14 should attempt actual `TimelineView` generation.

## Conditions to Proceed

- `TimelineView` type resolution succeeded
- `TimelineViewModel` type resolution succeeded
- Constructor candidates are discoverable
- Constructor binding dry-run resolves required parameters (or safe substitutes)
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

## preview14 Dry-run Signals

- Readiness score target: `>= 70` for considering isolated generation in preview15
- Blocking reasons must be explicit and actionable
- Optional/nullable parameters may be considered resolvable in dry-run
- Unknown required parameters are treated as blockers

## Safety Constraints

- Experimental mode stays disabled by default
- Reflection failures are treated as normal failures
- Adapter boundary (`IPureTimelineAdapter`) must absorb all failures
- `ProjectDiffWindow` and DiffTL must remain usable regardless of probe result

## preview15 Gate and Attempt Policy

- Generation attempt remains disabled by default.
- Gate conditions:
  - `EnableExperimentalYmmTimelineHost == true`
  - `AllowViewModelGenerationAttempt == true`
  - reflection probe + constructor dry-run completed
  - readiness score >= `MinimumReadinessScoreForGeneration`
  - `CanAttemptViewModelGeneration == true`
- If gate passes, only `TimelineViewModel` generation is attempted in isolated host.
- `TimelineView` generation is still out-of-scope.
- Successful generation is followed by immediate dispose verification.

## preview16 Runtime Guidance

- Benchmark runtime does not usually load YMM4 internal assemblies.
- `RuntimeKind=Benchmark` and missing Timeline types is expected.
- Run probe inside YMM4 plugin runtime to validate Timeline type visibility.
- Diagnostics now include runtime metadata and runtime-aware filenames.
