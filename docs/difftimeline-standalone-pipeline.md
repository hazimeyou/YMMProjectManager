# DiffTimeline Standalone Pipeline

## Overview

RouteA establishes a standalone DiffTimeline pipeline that does not depend on WPF, YMM runtime objects, or TimelineView integration.

Pipeline flow:

`DiffTimelineProjectSnapshot -> SnapshotDiffBuilder -> SemanticDiff -> CoreBuilder -> RowSet -> Summary/Diagnostics`

## Current Status

- RouteA standalone pipeline: **shadow validation ready**
- Manual opt-in UI route: **ready (guarded)**
- Formal UI route switch: **not default**
- TimelineView integration: **frozen**
- Default state: **disabled by default**

## Guarded Manual Route Selection

- Env flag for route request: `YMM_STANDALONE_DIFFTIMELINE_ROUTE=1`
- Env flag for shadow validation: `YMM_STANDALONE_SHADOW_VALIDATION=1`
- Standalone route is selected only when:
  1. manual route flag is enabled
  2. standalone pipeline envelope succeeds
  3. promotion gate returns allowed (no blockers)
- Otherwise, system always falls back to existing route.

`DiffTimelineRouteSelectionResult` records:

- requested route
- selected route
- fallback route
- reason
- promotion readiness snapshot
- diagnostics path

## Promotion Gate

`DiffTimelineStandalonePromotionGate` evaluates readiness and blocks route promotion when blockers exist.

- blockers => forced fallback
- warnings => diagnostics only

## Components

- Models: `Application/TimelineCore/Models`
  - Snapshot DTOs
  - Semantic diff models
  - Core/row models
  - Pipeline diagnostics models
  - Pipeline envelope models
  - Validation comparer/readiness/route-selection models
- Adapters: `Application/TimelineCore/Adapters`
  - `YmmNormalizedJsonSnapshotAdapter`
- Providers: `Application/TimelineCore/Providers`
  - `InMemoryDiffTimelineSnapshotProvider`
  - `SampleDiffTimelineSnapshotFactory`
- Builders: `Application/TimelineCore/Builders`
  - `DiffTimelineSnapshotDiffBuilder`
  - `DiffTimelineCoreBuilder`
  - `DiffTimelineCoreRowBuilder`
- Pipeline: `Application/TimelineCore/Pipeline`
  - `BuildFromSnapshots(...)`
  - `BuildEnvelopeFromSnapshots(...)`
- Caching: `Application/TimelineCore/Caching`
  - `IDiffTimelineSnapshotCache`
  - `DiffTimelineSnapshotCacheKeyFactory`
  - `InMemoryDiffTimelineSnapshotCache`
- Diagnostics: `Application/TimelineCore/Diagnostics`
  - `DiffTimelineStandalonePipelineSelfCheck`
  - `DiffTimelineStandalonePipelineDiagnosticsWriter`
  - `DiffTimelineValidationComparer`
  - `DiffTimelinePromotionReadinessEvaluator`

## Diagnostics Scope

Diagnostics JSON includes:

- existing route summary
- standalone route summary
- comparer result
- promotion readiness
- route selection
- cache hit/miss
- adapter diagnostics
- fallback/failure reason
- environment flag snapshot

## Safety and Boundaries

- Core pipeline is UI-independent and runtime-independent.
- Experimental outputs remain isolated under Experimental paths.
- Runtime bridge is intentionally not re-enabled.
- PlaceholderAdapter fallback is preserved.
- ProjectDiffWindow route is preserved.

## Default-Disabled Policy

- `EnableExperimentalYmmTimelineHost=false`
- `AllowViewModelGenerationAttempt=false`
- Standalone route and shadow mode are opt-in only.

## Formal Promotion Conditions (Draft)

1. Promotion gate passes without blockers.
2. Comparer confidence above threshold.
3. Missing rows trend remains near zero.
4. Diagnostics stability over repeated runs.

Until those conditions are met, existing route remains primary.
