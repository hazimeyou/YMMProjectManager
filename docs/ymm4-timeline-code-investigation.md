# YMM4-Timeline Code Investigation Plan (preview10)

Target: [routersys/YMM4-Timeline](https://github.com/routersys/YMM4-Timeline)

## Code-Level Questions

- TimelineView を生成しているクラスはどこか
- TimelineViewModel 生成時の必須引数は何か
- `SetTimelineToolInfo` 相当の入口はどこか
- Scene 切替時の再生成とイベント解除はどう処理しているか
- Dispose の責務境界はどこか

## Integration Barriers for YMMProjectManager

- YMM本体依存 DLL のバージョン差分
- `UndoRedoManager` / `AsyncAwaitStatus` の安全な受け渡し
- Scene コンテキスト解決方法の差異
- plugin 前提コードと standalone 配布要件の差

## Reuse Policy

- DiffTimeline への直接流用はしない
- Pure Timeline 側だけを対象に参考利用する
- 直接依存より Adapter 経由の差し替えを優先する

## License Notes

- MIT License の表記を維持する
- 参考実装の引用箇所は docs 側で追跡可能にする

## Preview10 Readiness Checks

- `IPureTimelineAdapter` で受け皿を用意済みか
- Placeholder fallback が実装済みか
- 初期化失敗時でも DiffTL 単体動作が維持されるか
