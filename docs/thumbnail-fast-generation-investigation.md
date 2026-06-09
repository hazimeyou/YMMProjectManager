# Thumbnail Fast Generation Investigation

## Goal

Validate the experimental fast thumbnail path without changing the default behavior.

- `ExperimentalFastThumbnailGenerationEnabled` stays `false`
- Existing thumbnail generation remains available
- RouteA / DiffTimeline / TimelineView integration is untouched
- Runtime bridge, SendInput, clipboard-heavy automation, and project content changes are out of scope

## RC5 Fix Result

RC5 fixed the discovery regression caused by the `GetHbitmap()` reflection path.

Current confirmed state:

- `PreviewViewFound = true`
- `PreviewViewModelFound = true`
- `GetBitmapMethodFound = true`
- `GetBitmapParameterTypes = ["System.Boolean"]`
- `GetBitmapInvocationSucceeded = true`
- `CaptureSucceeded = true`
- `GetBitmap(false)` succeeded
- `GetBitmap(true)` succeeded
- `PreferredCall = "GetBitmap(true)"`
- `warning = 0`
- `error = 0`

`GetBitmap(true)` is currently the preferred call because both calls produced the same dimensions and the `true` path was selected as the faster invocation.

## Validation RC1

RC1 adds benchmark-oriented validation for the fast thumbnail path.

Measured items:

- `ProjectPath`
- `SampleCount`
- `RequestedFrameCount`
- `CapturedFrameCount`
- `FailedFrameCount`
- `RetryCount`
- `TotalDurationMs`
- `AverageSeekDurationMs`
- `AverageSettleDurationMs`
- `AverageCaptureDurationMs`
- `AverageSaveDurationMs`
- `FallbackUsed`
- `FallbackReason`
- `PreferredGetBitmapCall`
- `BitmapWidth`
- `BitmapHeight`
- `BitmapPixelFormat`
- `GeneratedAt`

Optional additional items:

- `MinCaptureDurationMs`
- `MaxCaptureDurationMs`
- `MinSeekDurationMs`
- `MaxSeekDurationMs`
- `MinSettleDurationMs`
- `MaxSettleDurationMs`
- `MinSaveDurationMs`
- `MaxSaveDurationMs`
- `FramesPerSecondEffective`

Benchmark sample counts:

- `16`
- `32`
- `64`
- `128`
- `256`

Delay sweep:

- `0ms`
- `25ms`
- `50ms`
- `100ms`

## Output Location

Benchmark outputs are written to:

`%TEMP%\YMMProjectManager\thumbnail-fast-generation\`

Expected files:

- `benchmark-YYYYMMDD-HHMMSS.json`
- `benchmark-summary.json` (summary-only report)
- `capture-frames\...`

The `capture-frames` directory may contain only the selected representative frames if full persistence is not enabled.

## Legacy Comparison

Legacy comparison is supported as metadata, but clipboard-based legacy execution is intentionally skipped by default.

- `LegacyTotalDurationMs` may be `null`
- `SpeedupRatio` may be `null`
- The benchmark still records the fast-path timings and fallback reason

## Known Constraints

- The benchmark runner is validation-oriented and should be invoked from a developer hook or test harness
- If the WPF dispatcher or YMM4 runtime is unavailable, the run should fail gracefully and still keep the diagnostics consistent
- `GetBitmap(true)` remains the preferred fast-path call

## Next Phase Conditions

Proceed only if the benchmark confirms:

- stable discovery
- repeated capture success across the selected sample counts
- acceptable retry behavior with the configured settle delays
- no regression in the existing thumbnail path
