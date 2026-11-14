# Snapshot 保護

保存場所は `projects/<projectId>/generations/<generationId>/project.ymmp` と `metadata.json`。作成・削除の異常 fixture では元 snapshot と metadata の SHA-256 不変、新規 snapshot ディレクトリ増加なしを確認。既存の直接復元は manifest を参照せず metadata の SHA-256 を照合し、別名ファイルへ復元できた。正常 manifest での元ファイルへの復元は既存テストで確認。

不正 JSON の実行証拠: manifest 前後 `40B5CAFE045B9C9E31A1C3FDCFBC89F934906FF06A6276B55C2A0E517BE21655`、snapshot 前後 `10718FA622E1B1BD9C9EF5F1AB5C96CAE1DAA6461D00B0E7D214780ABF88429F`。合成 fixture のみ。manifest 欠落後の直接復元は service API を使用しており、一覧 UI から旧 ID を選択できる意味ではない。
