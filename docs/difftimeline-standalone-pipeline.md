# DiffTimeline Standalone Pipeline

## Overview

RouteA establishes a standalone DiffTimeline pipeline that does not depend on WPF, YMM runtime objects, or TimelineView integration.

Pipeline flow:

`DiffTimelineProjectSnapshot -> SnapshotDiffBuilder -> SemanticDiff -> CoreBuilder -> RowSet -> Summary/Diagnostics`

## Current Status

- RouteA standalone pipeline: **shadow validation ready**
- Formal UI route switch: **not enabled**
- TimelineView integration: **frozen**
- Default state: **disabled by default**

## Components

- Models: `Application/TimelineCore/Models`
  - Snapshot DTOs
  - Semantic diff models
  - Core/row models
  - Pipeline diagnostics models
  - Pipeline envelope models
  - Validation comparer/readiness models
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

## Shadow Validation Mode

`ProjectDiffViewModel` keeps existing display route unchanged, and can run standalone pipeline in shadow mode (private/default false).

Shadow diagnostics include:

- existing route summary
- standalone route summary
- comparer result
- promotion readiness
- cache hit/miss
- adapter diagnostics
- fallback/failure reason

## Promotion Readiness

`DiffTimelineStandalonePromotionReadiness` evaluates:

- `canPromote`
- blockers/warnings
- confidence
- cache status
- comparer result
- fallback reason

This is used only for diagnostics and decision support. It does not switch UI route by itself.

## Safety and Boundaries

- Core pipeline is UI-independent and runtime-independent.
- Experimental outputs remain isolated under Experimental paths.
- Runtime bridge is intentionally not re-enabled.
- PlaceholderAdapter fallback is preserved.
- ProjectDiffWindow route is preserved.

## Default-Disabled Policy

- `EnableExperimentalYmmTimelineHost=false`
- `AllowViewModelGenerationAttempt=false`
- Standalone shadow mode switch remains default `false`.

## Formal Switch Conditions (Draft)

1. Comparer key match rate stable above threshold.
2. Missing rows trend remains near zero.
3. Promotion readiness has no blockers.
4. Cache/diagnostics stability confirmed over repeated runs.

Until those conditions are met, existing route remains primary.
