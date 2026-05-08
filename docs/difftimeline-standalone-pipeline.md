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
- Adapters: `Application/TimelineCore/Adapters`
  - `YmmNormalizedJsonSnapshotAdapter` (normalized JSON -> snapshot)
- Providers: `Application/TimelineCore/Providers`
  - `InMemoryDiffTimelineSnapshotProvider`
  - `SampleDiffTimelineSnapshotFactory`
- Builders: `Application/TimelineCore/Builders`
  - `DiffTimelineSnapshotDiffBuilder`
  - `DiffTimelineCoreBuilder`
  - `DiffTimelineCoreRowBuilder`
- Pipeline: `Application/TimelineCore/Pipeline`
  - `DiffTimelineStandalonePipeline.BuildFromSnapshots(...)`
- Serialization: `Application/TimelineCore/Serialization`
  - `DiffTimelineSnapshotJsonSerializer`
- Caching skeleton: `Application/TimelineCore/Caching`
  - `IDiffTimelineSnapshotCache`
  - `DiffTimelineSnapshotCacheKeyFactory`
- Diagnostics: `Application/TimelineCore/Diagnostics`
  - `DiffTimelineStandalonePipelineSelfCheck`
  - `DiffTimelineStandalonePipelineDiagnosticsWriter`

## Adapter/Hash/Cache Foundation

- Snapshot metadata carries `SnapshotHash` for identity and comparison.
- Diagnostics include:
  - snapshot source
  - adapter source
  - conversion result
  - skipped/unsupported fields
  - old/new snapshot hash
  - pipeline result hash
- Cache key foundation:
  - `key = oldSnapshotHash + newSnapshotHash + optionsHash`

## Safety and Boundaries

- Core pipeline is UI-independent and runtime-independent.
- TimelineView integration remains frozen (research-only / disabled path).
- Experimental outputs remain isolated under Experimental paths.
- PlaceholderAdapter fallback is preserved.
- Default-disabled policy remains unchanged:
  - `EnableExperimentalYmmTimelineHost=false`
  - `AllowViewModelGenerationAttempt=false`

## Current Validation Path

`ProjectDiffViewModel` includes a private validation helper:

1. Tries real-data snapshot conversion via normalized-json adapter when paths are available.
2. Falls back to sample snapshot when unavailable.
3. Runs standalone pipeline and self-check.
4. Writes diagnostics JSON.

Existing diff display route is preserved, and any validation failure keeps fallback active.

## Next Steps

1. Wire project/snapshot path discovery to feed real old/new files automatically.
2. Increase semantic diff precision while keeping deterministic rules.
3. Add dedicated unit tests for adapter conversion and cache-key stability.
