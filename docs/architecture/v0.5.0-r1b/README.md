# R1-B 世代 Manifest 保護 — PARTIAL

基準 `0690b3a41efbf981784078b880ef65aec14e44c3`（R1-A2、R1-A を祖先に含む）。2026-11-14 取得の origin/master は `bdf6dbd32c910bd2a9ea6432399ba40f8f1d72af`。旧世代機能の拡張・移行は行わない。

破損・未対応・欠落と旧データ残存を正常空一覧と区別し、作成・削除と repository の保存を停止する。合成 fixture による回帰と Release build は成功。書込み途中の I/O 故障、実ユーザーデータ互換性と YMM4 実ホストは未検証。R1-A2 の実機受入は別途未完了。

- [状態・読込](01-manifest-state-analysis.md)
- [保護設計](02-corruption-protection-design.md)
- [Snapshot 保護](03-snapshot-preservation.md)
- [テスト結果](04-test-results.md)
- [制約](05-known-limitations.md)
