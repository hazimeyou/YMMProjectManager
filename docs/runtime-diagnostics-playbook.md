# Runtime Diagnostics Playbook

## Purpose

This page consolidates the runtime diagnostics procedure for history-preview timeline investigation.

Scope:

- reflection probe
- constructor binding dry-run
- runtime dependency discovery (`Scene`, `UndoRedoManager`, `AsyncAwaitStatus`)
- strict generation gate validation

Out of scope:

- Timeline visual attach
- production timeline integration
- forced instance creation

## Safety Defaults

- `EnableExperimentalYmmTimelineHost = false`
- `AllowViewModelGenerationAttempt = false`
- Fallback first: DiffTimeline standalone must remain usable

## Run Steps

1. Open YMM4 and open a project.
2. Open `ProjectDiffWindow`.
3. Click `診断ホストを開く`.
4. In host window:
   1. `実行環境を再判定`
   2. `Reflection Probe 実行`
   3. `Binding Dry-run 実行`
   4. Optional: `生成試行(即破棄)` (explicit action only)
   5. `Diagnostics JSON 保存`

## Log Location

Primary path:

`YMMProjectManager\bin\Debug\net10.0-windows\logs\diagnostics\`

Typical files:

- `timeline-probe-YMM4Plugin-*.json`
- `timeline-binding-YMM4Plugin-*.json`
- `timeline-generation-attempt-YMM4Plugin-*.json`

## How To Read JSON Quickly

### 1. Runtime check

- `runtimeKind == "YMM4Plugin"`
- `processName == "YukkuriMovieMaker"`

### 2. Type visibility

- `reflection.TimelineViewFound`
- `reflection.TimelineViewModelFound`

### 3. Strict dependency gate

Check `timelineViewModelConstructorBindings[].Parameters[]`:

- `IsRequiredYmmRuntimeDependency`
- `CanResolve`
- `FailureReason`

Expected in strict mode when unresolved:

- `CanResolve = false`
- generation skipped

### 4. Discovery summary

Use summary first:

- `summary.sceneResolved`
- `summary.undoRedoResolved`
- `summary.asyncAwaitResolved`

Then drill down:

- `reflection.SceneDiscovery`
- `reflection.UndoRedoManagerDiscovery`
- `reflection.AsyncAwaitStatusDiscovery`

Key fields:

- `resolved`
- `candidateOwners`
- `staticProperties`
- `instanceFields`
- `resolutionAttempts`

### 5. Next-step decision

Do not retry generation until all are true:

- `SceneDiscovery.resolved`
- `UndoRedoManagerDiscovery.resolved`
- `AsyncAwaitStatusDiscovery.resolved`

## Known unresolved pattern (current)

- Reflection and constructor discovery succeed.
- Runtime dependencies remain unresolved in isolated context.
- Gate keeps generation skipped with explicit reason.
- Fallback path stays healthy.

## Troubleshooting

- If host open/probe feels frozen:
  - wait for progress popup completion
  - rerun after reducing traversal cost settings (see notes below)

- If diagnostics are missing:
  - run probe once first
  - press save button after probe

## Discovery cost notes

Current runtime discovery is intentionally bounded:

- depth limit
- node cap
- owner type exclude prefixes (framework-heavy types)

These limits prioritize UI safety over coverage and can miss deep routes by design.
