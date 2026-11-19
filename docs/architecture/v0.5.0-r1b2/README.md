# R1-B2 — Manifest 保存の障害・競合対策

判定: **PARTIAL**。基準 `fbb5e13b84aeaedc35aabb21a438e2fb35c2ea95` は R1-A / R1-A2 を祖先に含む。2026-11-16 取得の origin/master は `bdf6dbd32c910bd2a9ea6432399ba40f8f1d72af`。

R1-B では固定 `.tmp` の上書き・削除と、読込後の有効な外部変更の消失を合成 fixture で再現した。修正後は Manifest ごとのプロセス内保存直列化、一意な一時ファイル、読込時と置換直前の SHA-256 比較を行う。部分書込み・flush 失敗や競合を独立テストで検証。比較と置換の間の別プロセス競合、電源断、実ホストは未保証。R1-A2 の実機受入も別途未完了。

[保存経路](01-write-path-analysis.md)・[障害注入](02-fault-injection-results.md)・[並行](03-concurrency-analysis.md)・[外部変更](04-external-modification.md)・[Snapshot](05-snapshot-preservation.md)・[テスト](06-test-results.md)・[制約](07-known-limitations.md)
