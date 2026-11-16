# Checkpoint 復元 — BLOCKED

VM が存在しないため checkpoint 作成、復元、復元後起動の実測はない。再開時は専用 VM のみを対象に clean baseline の checkpoint を作り、guest 内の合成 sentinel に変更を加えてから復元し、sentinel と guest の実効状態を確認する。復元対象の VM ID と checkpoint を確認し、既存 VM・ホスト本番ファイルには適用しない。実行前に仮想ディスク容量と checkpoint 増分の空きを確保する。
