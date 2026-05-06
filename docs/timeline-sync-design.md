# Timeline Sync Design (preview8)

## Scope

This document defines a staged sync design for `Pure Timeline + Diff Timeline`.
Preview8 is design + one-way PoC, not full integration.

## Sync Channels

1. Frame Sync
- Priority: highest
- Direction in preview8: Pure placeholder -> Diff timeline
- Mechanism: `PureTimelineCurrentFrame` pushes `DiffTimelineViewModel.SetCurrentFrame(...)`

2. Selection Sync
- Priority: medium
- Direction in preview8: Diff list <-> Diff timeline
- Future extension: Pure timeline selection -> Diff selection mapping

3. Scroll/Visible Range Sync
- Priority: lower
- Direction in preview8: local Diff timeline only
- Future extension: pure timeline viewport -> diff viewport bridge

## Sync State Model

- `Unavailable`: pure timeline source not connected
- `Detached`: source exists but not syncing
- `Synced`: frame bridge active

## Timeline Mode Model

- `Standalone`
- `Synced`
- `Comparison`

## Planned Evolution

- v0.3.x: stabilize one-way frame sync + selection mapping hooks.
- v0.4.0: optional pure timeline panel with controlled two-pane behavior.

## Non-Goals (current preview)

- full bidirectional live sync
- overlay editing
- replay/restore workflows
