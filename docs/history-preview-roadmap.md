# History Preview Roadmap

## Positioning

`v0.2.9-history-preview` は正式仕様ではなく、設計検証のための preview 系列です。

## Priorities

1. 基盤安定化（最優先）
2. YMM意味差分の精度向上
3. 純正TL + DiffTL 構成の段階導入

## Timeline Policy

- 純正TL側: YMM4-Timeline の設計（SetTimelineToolInfo, Dispose, scene切替処理）を参考に再実装
- DiffTL側: Snapshot差分専用の自作読み取り専用Timeline

preview7 で追加したUX:

- Zoom / Scale UI
- Timeline ruler/header
- 実験的 grouping
- 選択同期とナビゲーション強化

## YMM4-Timeline Investigation Status

使える部分:

- `SetTimelineToolInfo` による scene 解決パターン
- `TimelineViewModel(scene, UndoRedoManager, AsyncAwaitStatus)` の生成フロー
- scene切替時の `Dispose + PropertyChanged解除`

使わない部分:

- そのままの plugin 依存構成
- DiffTimeline への直接流用

依存リスク:

- YMM4 本体DLL依存が多く、直接参照は更新耐性を下げる
- YMM4 バージョン差異で破綻しやすい

YMMProjectManager 方針:

- `YMM4-Timeline ≠ DiffTimeline`
- DiffTimeline は自作継続
- 純正TL連携は将来 optional に段階導入

## Release Plan

### v0.3.x

- 基盤安定化と計測
- 差分モデル改善
- UIは必要最小限の改善に留める

### v0.4.0

- `純正TL + DiffTL + DiffDetailPanel` 構成の本格導入
- DiffTLの可視化強化
- restore / branch / merge は対象外のまま


## Preview8 Additions

- Pure Timeline investigation doc: docs/pure-timeline-investigation.md`n- Timeline sync design doc: docs/timeline-sync-design.md`n- One-way frame sync PoC added via placeholder panel.



## Preview9 Additions

- CurrentFrame navigation commands and frame-based diff jump
- SyncState/TimelineMode manual UI switching
- Pure timeline integration checklist (docs/pure-timeline-integration-checklist.md)

