# Pure Timeline Integration Checklist (preview11)

- TimelineView / TimelineViewModel を安全に生成できるか
- YMM本体の現在シーンを取得できるか
- Scene切替を検出できるか
- Dispose漏れを防げるか
- UndoRedoManager / AsyncAwaitStatus を安全に受け渡せるか
- YMM4本体のバージョン差に耐えられるか
- YMM4-Timelineのコードを参考利用する場合のMIT表記が整理されているか
- YMMProjectManager単体配布を壊さないか
- PureTLが失敗してもDiffTL単体で動くか
- Adapter境界（`IPureTimelineAdapter`）があるか
- Placeholder fallback（`PlaceholderPureTimelineAdapter`）があるか
- Initialize失敗時に `ProjectDiffWindow` を閉じず継続できるか
- Dispose失敗時にアプリを落とさないか
- 将来 `FutureYmmTimelineAdapter` へ差し替え可能か
- `PureTimelineAdapterKind` で切替できるか
- `FutureYmmTimelineAdapter` が安全に Fail するか
- Fail 後に Placeholder fallback が有効化されるか
- `LastError` / `FallbackActive` が UI で確認できるか
