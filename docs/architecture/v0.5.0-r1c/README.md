# R1-C 再リンク保存の保護 — PARTIAL

基準 `be9bed85be9d8e5f5c0799618a69011589967f53`（R1-B2、R1-A〜R1-B を祖先に含む）。2026-11-16 に確認した origin/master は `bdf6dbd32c910bd2a9ea6432399ba40f8f1d72af`。合成 fixture のみで検証し、本番データと本番 YMM4 を使用していない。

ファイル経路の対象値だけの更新、外部変更検出、一時ファイルと原子的置換、既存バックアップ保護を追加。PSD および VideoItem の PNG は未統合の既存作業を参考にしつつ、今回互換性が証明できないため保存を拒否。Timeline の特殊素材も事前拒否する。実ホスト、特殊ケースの正常動作、Timeline の通常更新と中途失敗の動作は未検証。

[処理経路](01-relink-path-analysis.md)・[JSON更新](02-targeted-json-update.md)・[外部変更](03-external-modification.md)・[保存](04-atomic-save.md)・[特殊パス](05-special-path-compatibility.md)・[テスト](06-test-results.md)・[制約](07-known-limitations.md)
