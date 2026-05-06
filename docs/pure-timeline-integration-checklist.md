# Pure Timeline Integration Checklist (preview9)

- TimelineView / TimelineViewModel を安全に生成できるか
- YMM本体の現在シーンを取得できるか
- Scene切替を検出できるか
- Dispose漏れを防げるか
- UndoRedoManager / AsyncAwaitStatus を安全に受け渡せるか
- YMM4本体のバージョン差に耐えられるか
- YMM4-Timelineのコードを参考利用する場合のMIT表記が整理されているか
- YMMProjectManager単体配布を壊さないか
- PureTLが失敗してもDiffTL単体で動くか
