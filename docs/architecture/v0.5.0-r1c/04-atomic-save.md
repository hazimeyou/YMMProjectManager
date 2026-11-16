# 保存と backup

更新対象検証・外部変更確認 → 更新 JSON の生成と parse → 同一ディレクトリに一意 temp を CreateNew → 書込、FlushAsync、Flush(true) → backup の排他的作成と内容 hash 確認 → 外部変更再確認 → File.Replace。対象を削除して移動する fallback はない。既存 `.bak` がなければ `.bak` を使用、既存なら `.bak-<GUID>` を作り、既存 backup を上書きしない。失敗時は自分の temp / backup だけを削除し、cleanup 失敗はログに記録する。競合条件による backup 名競合は失敗扱い。

合成の部分書込み / flush 例外、事前キャンセル、OS 共有違反で元ファイルの hash 不変を確認。File.Replace は filesystem や power loss について完全な耐久性を保証しない。cleanup 自体の故障は未注入。
