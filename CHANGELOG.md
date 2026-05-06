# Changelog

## v0.2.9-history-preview7

- Timeline UX 強化
  - Zoom In / Zoom Out / Reset Zoom
  - Ctrl + MouseWheel zoom
  - 選択中Diffのハイライト強調
  - 選択時センタースクロール改善
- Timeline ruler/header を追加
  - Scale に応じた marker interval（1000 / 300 / 30 frame）
- Diff grouping（最小版）を追加
  - `Text Changes`
  - `FilePath Changes`
  - `Timeline Moves`
  - `Length Changes`
  - `Other Changes`
- visible range filtering を継続強化
  - Visible item count の観測を追加
- Benchmark 強化
  - projection count / visible item count
  - zoom recalculation time
  - grouping time
- YMM4-Timeline 調査方針の整理をドキュメントへ追記

## v0.2.9-history-preview6

- DiffTimelineView を大規模運用向けに強化
  - visible range filtering（Frame/Layer 範囲外は描画しない）
  - `Scale / RowHeight / VisibleStartFrame / VisibleEndFrame / VisibleMinLayer / VisibleMaxLayer` を ViewModel に追加
  - 次/前Diffナビゲーションと選択Diffへのスクロールを追加
- ProjectDiffWindow の一覧選択と Timeline 選択の同期土台を追加
- ベンチマーク拡張
  - DiffTimeline projection time
  - visible item filtering time
  - large/extreme シナリオで確認可能

## v0.2.9-history-preview5

- Added Internal ID match statistics
- Added diff correctness benchmark fixtures
- Added correctness benchmark output
- Added experimental `DiffTimelineView` prototype
- Continued YMM4-Timeline investigation
