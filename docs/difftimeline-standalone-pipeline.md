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
  - Pipeline envelope models
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
  - `DiffTimelineStandalonePipeline.BuildEnvelopeFromSnapshots(...)`
- Serialization: `Application/TimelineCore/Serialization`
  - `DiffTimelineSnapshotJsonSerializer`
- Caching: `Application/TimelineCore/Caching`
  - `IDiffTimelineSnapshotCache`
  - `DiffTimelineSnapshotCacheKeyFactory`
  - `InMemoryDiffTimelineSnapshotCache`
- Diagnostics: `Application/TimelineCore/Diagnostics`
  - `DiffTimelineStandalonePipelineSelfCheck`
  - `DiffTimelineStandalonePipelineDiagnosticsWriter`

## Adapter/Hash/Cache Foundation

- Snapshot metadata carries `SnapshotHash` for identity and comparison.
- Cache key shape:
  - `oldSnapshotHash + newSnapshotHash + optionsHash`
- Hash generation is stable via ordered option serialization.
- Diagnostics include:
  - snapshot source
  - adapter source
  - conversion result
  - skipped/unsupported fields
  - old/new snapshot hash
  - pipeline result hash
  - cache hit/miss
  - cache key

## Pipeline Result Envelope

`DiffTimelineStandalonePipelineEnvelope` returns:

- `Result` (nullable)
- `CacheHit`
- `SnapshotSource`
- `FallbackReason`
- `IsSuccess`
- `Errors`
- `Warnings`

This allows validation and diagnostics even when pipeline processing fails.

## Validation Status Path

`ProjectDiffViewModel` private validation helper now:

1. Tries normalized-json adapter path if old/new file paths are available.
2. Falls back to sample snapshot when unavailable.
3. Uses pipeline envelope + in-memory cache.
4. Stores a private validation status model (`DiffTimelineStandaloneValidationStatus`).
5. Writes diagnostics JSON with fallback reason and source details.

Existing display route and fallback behavior are preserved.

## Current Limits

- TimelineView integration remains frozen (research-only / disabled path).
- Experimental outputs remain isolated under Experimental paths.
- Runtime bridge is intentionally not re-enabled.
- RouteA is standalone-first; formal UI route switching remains default-off.

## Default-Disabled Policy

- `EnableExperimentalYmmTimelineHost=false`
- `AllowViewModelGenerationAttempt=false`
- Any standalone switch preparation remains default `false`.

## Next Steps

1. Wire project/snapshot path discovery so validation can use real old/new files automatically.
2. Persist standalone cache across view-model instances (optional).
3. Add unit tests for envelope error path and diagnostics metadata completeness.
