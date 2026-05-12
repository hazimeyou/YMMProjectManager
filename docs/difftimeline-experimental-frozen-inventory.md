# DiffTimeline Experimental / Frozen Inventory

## Scope
- Target date: 2026-05-12
- Branch: `cleanup/routea-aggressive-safe-reduction`
- Policy: inventory only (no feature add / no UI change / no large deletion)

## Stabilization Checkpoint
- Latest cleanup commits:
  - `8b4c170` refactor(timeline): consolidate workspace state refresh flow
  - `11cda60` docs(timeline): summarize RouteA cleanup handoff state
- Build check after YMM lock release: `dotnet build YMMProjectManager.sln` => 0 warning / 0 error

## Classification Rule
- `D1`: frozen keep / deletion prohibited
- `D2`: future deletion candidate (not this phase)
- `D3`: docs/comment cleanup candidate
- `D4`: needs human review

## Inventory (Presentation/Timeline/**)

| File | Lines | Role | Referenced | Frozen Scope | Class | Recommended Action |
|---|---:|---|---|---|---|---|
| `Presentation/Timeline/Experimental/ViewModels/ExperimentalYmmTimelineHostViewModel.cs` | 2427 | Experimental host diagnostics/investigation VM | Yes (`OpenExperimentalDiagnosticsHost`) | Yes | D1 | Keep frozen; no behavioral edits |
| `Presentation/Timeline/Experimental/ViewModels/PureTimelineHostViewModel.cs` | 218 | Placeholder/future adapter host VM | Yes (`ProjectDiffViewModel`) | Yes (experimental boundary) | D1 | Keep |
| `Presentation/Timeline/Experimental/Views/ExperimentalYmmTimelineHostWindow.xaml` | 140 | Experimental host view | Yes | Yes | D1 | Keep |
| `Presentation/Timeline/Experimental/Views/ExperimentalYmmTimelineHostWindow.xaml.cs` | 159 | Experimental host code-behind | Yes | Yes | D1 | Keep |
| `Presentation/Timeline/Experimental/README.md` | 8 | Experimental note | N/A | Yes | D3 | Minor doc refresh only |
| `Presentation/Timeline/YmmTimelineViewGenerationAttempt.cs` | 1286 | TimelineView/runtime bridge investigation + guard rail outputs | Yes | Yes (explicitly non-embedding, non-injection) | D1 | Keep as frozen evidence |
| `Presentation/Timeline/YmmTimelineViewGenerationAttemptResult.cs` | 687 | Investigation result model set | Yes | Yes | D1 | Keep |
| `Presentation/Timeline/YmmRuntimeDependencyDiscoveryService.cs` | 323 | Runtime dependency discovery | Yes | Yes | D1 | Keep |
| `Presentation/Timeline/YmmTimelineReflectionProbe.cs` | 313 | Reflection probe for runtime observation | Yes | Yes | D1 | Keep |
| `Presentation/Timeline/FutureYmmTimelineAdapter.cs` | 147 | Future adapter stub/integration placeholder | Yes (via adapter kind surface) | Yes | D2 | Candidate for later extraction after policy change |
| `Presentation/Timeline/YmmTimelineViewModelGenerationAttempt.cs` | 160 | ViewModel generation investigation | Yes | Yes | D2 | Keep now; review in dedicated phase |
| `Presentation/Timeline/YmmTimelineDependencyResolver.cs` | 135 | Runtime timeline dependency resolver | Yes | Yes | D1 | Keep |
| `Presentation/Timeline/YmmTimelineConstructorBinder.cs` | 94 | Constructor binding probe | Yes | Yes | D2 | Keep; future review |
| `Presentation/Timeline/RuntimeEnvironmentDetector.cs` | 61 | Runtime env detect helper | Yes | Boundary utility | D4 | Human review before any reduction |
| `Presentation/Timeline/PlaceholderPureTimelineAdapter.cs` | 62 | Current safe adapter | Yes (active path) | No (active) | D1 | Keep (active) |
| `Presentation/Timeline/IPureTimelineAdapter.cs` | 11 | Adapter contract | Yes | Active contract | D1 | Keep |
| `Presentation/Timeline/PureTimelineExperimentalOptions.cs` | 20 | Experimental toggles container | Yes | Yes | D1 | Keep |
| `Presentation/Timeline/PureTimelineDiagnostics.cs` | 106 | Diagnostics model for timeline adapter | Yes | Mixed | D4 | Human review for future split |
| `Presentation/Timeline/PureTimelineAdapterResult.cs` | 24 | Adapter execution result | Yes | Active contract | D1 | Keep |
| `Presentation/Timeline/PureTimelineAdapterKind.cs` | 6 | Adapter enum | Yes | Active | D1 | Keep |
| `Presentation/Timeline/PureTimelineStatus.cs` | 11 | Host status enum | Yes | Active | D1 | Keep |
| `Presentation/Timeline/RuntimeEnvironmentKind.cs` | 8 | Env enum | Yes | Utility | D1 | Keep |
| `Presentation/Timeline/YmmRuntimeDependencyCandidate.cs` | 15 | Discovery model | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmRuntimeDependencyDiscoveryOptions.cs` | 16 | Discovery options | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmRuntimeDependencyDiscoverySummary.cs` | 17 | Discovery summary model | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmTimelineConstructorBindingResult.cs` | 10 | Binding result model | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmTimelineConstructorParameterResult.cs` | 12 | Binding parameter model | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmTimelineGenerationAttemptResult.cs` | 28 | Attempt result wrapper | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmTimelineGenerationReadiness.cs` | 13 | Readiness model | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmTimelineInstanceDisposer.cs` | 35 | Runtime instance disposer helper | Yes | Frozen context | D2 | Keep; later risk review |
| `Presentation/Timeline/YmmTimelineReflectionLog.cs` | 11 | Reflection log model | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmTimelineReflectionResult.cs` | 30 | Reflection probe result model | Yes | Frozen context | D1 | Keep |
| `Presentation/Timeline/YmmTimelineViewGenerationAttemptResult.cs` | 687 | Detailed investigation output models | Yes | Yes | D1 | Keep |
| `Presentation/Timeline/YmmTimelineVisualSafetyGuard.cs` | 6 | Visual attach guard | Yes | Safety boundary | D1 | Keep |

## Frozen-Critical Cross References (non-delete)
- `Application/TimelineCore/Diagnostics/DiffTimelinePreviewValidationRunner.cs`
  - `TimelineViewIntegrationFrozen = true`
  - `RuntimeBridgeFrozen = true`
  - `ExperimentalUntouched = true`
- `Application/TimelineCore/Diagnostics/DiffTimelineDiagnosticsExportPackageWriter.cs`
  - `timelineViewIntegrationFrozen = true`
- `Application/TimelineCore/Models/DiffTimelineRcValidationSummaryModels.cs`
  - frozen-policy fields retained

## Deletion Prohibited (Reconfirmed)
- TimelineView integration frozen code
- runtime bridge frozen code
- production embedding guard path
- fallback/default-disabled path
- RC metadata / `rc-validation-summary`
- diagnostics/export foundation
- Large Result Mode + row window controls
- session save/load
- validation logging

## Next Safe Actions
1. `D3` only: doc/comment clarity updates.
2. `D4` review: small architecture review with human sign-off before any code reduction.
3. Keep `D1/D2` untouched until frozen policy is explicitly changed.
