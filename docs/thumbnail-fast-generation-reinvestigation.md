# Thumbnail Fast Generation Stabilize RC1

## Purpose

Keep the pieces that already work for thumbnail generation and freeze unstable seek logic out of the main path.

## Branch

- `feature/thumbnail-fast-generation-investigation`

## Current Status

- Current preview capture: success
- `GetBitmap(true)`: success
- Timeline last frame probe: success
- Thumbnail generation: success
- Seek logic: no-go

## No-Go

- Absolute Seek: No-Go
- Relative Seek: No-Go
- Reason: unstable in real device verification

## Kept

- `CurrentPreviewCaptureService`
- `YmmPreviewDiscoveryService`
- `YmmPreviewBitmapCaptureAdapter`
- `TimelineDurationProbeService`
- Thumbnail generation logic

## Removed from Main Path

- Seek probe UI
- Current frame probe UI
- Absolute seek implementation
- Relative seek implementation

## Preview Capture Check

1. Open YMM4 and load a project.
2. Run `現在プレビュー取得`.
3. Confirm a PNG and JSON are written under `%TEMP%\YMMProjectManager\current-preview-capture\`.
4. Confirm `NextRecommendedCall = GetBitmap(true)`.
5. Confirm the saved PNG matches the visible preview.

## Last Frame Check

1. Open YMM4 and load a project.
2. Run `最終フレーム取得`.
3. Confirm the JSON contains `Success = true` and a valid `LastFrame`.
4. `LastFrame` may be off by about `-1F`; that level of error is acceptable for this investigation.

## Success Conditions

- Current preview capture succeeds.
- Timeline last frame probe succeeds.
- Thumbnail generation succeeds.
- `warning = 0`.
- `error = 0`.

## Manual Output Paths

- Current preview capture: `%TEMP%\YMMProjectManager\current-preview-capture\`
- Timeline duration probe: `%TEMP%\YMMProjectManager\timeline-duration-probe\`

## Resume Conditions

Do not return to 64-point generation until the thumbnail pipeline is stable and reviewed.
