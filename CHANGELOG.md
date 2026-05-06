# Changelog

## v0.2.9-history-preview8

- Pure Timeline investigation docs added
  - `docs/pure-timeline-investigation.md`
  - `docs/timeline-sync-design.md`
- ProjectDiffWindow に Pure Timeline Placeholder panel を追加
  - Sync state
  - current frame bridge input
  - selected item state
- Timeline sync PoC (one-way frame sync)
  - placeholder frame -> DiffTL current frame line
- DiffTimeline sync-ready architecture update
  - `TimelineMode` / `TimelineSyncState`
  - current frame line rendering
- Benchmark強化
  - sync/zoom/grouping関連の指標を追加

## v0.2.9-history-preview7

- Timeline UX 強化
  - Zoom In / Zoom Out / Reset Zoom
  - Ctrl + MouseWheel zoom
  - 選択中Diffのハイライト強調
  - 選択時センタースクロール改善
- Timeline ruler/header を追加
- Diff grouping（最小版）を追加
- visible range filtering を継続強化
- Benchmark 強化
- YMM4-Timeline 調査方針の整理をドキュメントへ追記
