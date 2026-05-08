# DiffTimeline Standalone Pipeline

## Overview

RouteA establishes a standalone DiffTimeline pipeline that does not depend on WPF, YMM runtime objects, or TimelineView integration.

Pipeline flow:

`DiffTimelineProjectSnapshot -> SnapshotDiffBuilder -> SemanticDiff -> CoreBuilder -> RowSet -> Summary/Diagnostics`

## Components

- Models: `Application/TimelineCore/Models`
  - Snapshot DTOs (`DiffTimelineProjectSnapshot`, `DiffTimelineTimelineSnapshot`, `DiffTimelineLayerSnapshot`, `DiffTimelineItemSnapshot`)
  - Semantic diff models
  - Core result / row models
  - Pipeline diagnostics models
- Builders: `Application/TimelineCore/Builders`
  - `DiffTimelineSnapshotDiffBuilder`
  - `DiffTimelineCoreBuilder`
  - `DiffTimelineCoreRowBuilder`
- Pipeline: `Application/TimelineCore/Pipeline`
  - `DiffTimelineStandalonePipeline.BuildFromSnapshots(...)`
- Serialization: `Application/TimelineCore/Serialization`
  - `DiffTimelineSnapshotJsonSerializer`
- Diagnostics: `Application/TimelineCore/Diagnostics`
  - `DiffTimelineStandalonePipelineSelfCheck`
  - `DiffTimelineStandalonePipelineDiagnosticsWriter`

## Safety and Boundaries

- Core pipeline is UI-independent and runtime-independent.
- TimelineView integration remains frozen (research-only / disabled path).
- Experimental outputs remain isolated under Experimental paths.
- PlaceholderAdapter fallback is preserved.
- Default-disabled policy remains unchanged:
  - `EnableExperimentalYmmTimelineHost=false`
  - `AllowViewModelGenerationAttempt=false`

## Current Validation Path

`ProjectDiffViewModel` includes a private validation helper that runs the standalone pipeline with sample snapshots and writes diagnostics JSON.

- Existing diff display route is preserved.
- Failure in validation path does not break UI route.
- Fallback remains active on any exception.

## Next Steps

1. Add concrete snapshot provider backed by normalized project snapshots.
2. Increase semantic diff precision while keeping deterministic rules.
3. Add dedicated unit tests for pipeline diagnostics and summary counts.
