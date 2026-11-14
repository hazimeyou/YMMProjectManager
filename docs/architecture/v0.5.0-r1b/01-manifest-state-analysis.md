# Manifest 状態

保存先は `%AppData%/YMMProjectManager/Generations/projects/<projectId>/manifest.json`。Storage は開いて JSON を検査し schema 1、基礎フィールドと各世代の必須フィールド、型と基本値を確認する。初回生成時のモデルは履歴 `ab4abed` でも schema 1 と現在の同じフィールドだった。実データの全履歴形式を検証した意味ではない。

| 状態 | 結果 |
|---|---|
| ファイル不在、generations / deleted にデータなし | Missing、初回空 manifest を使用 |
| 不在で旧世代データあり | Corrupted、書込み拒否 |
| 正常空または既存 | Valid、一覧利用・書込み可能 |
| 不正 JSON / 空 / 欠落 / 型不正 / 読込 I/O | Corrupted、manifest は null |
| 未対応 schema | UnsupportedSchema、manifest は null |

キャンセルはエラー状態への変換をせず伝播。通常一覧は失敗を例外で伝播、診断は status を表示する。既存一覧 UI は失敗を警告し、空一覧の正常表示と区別する。UI 実ホスト未確認。
