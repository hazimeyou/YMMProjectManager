# 再リンク経路

`ProjectListViewModel` → `RelinkMainWindow(TimelineToolInfo, ...)` は現在開いた Timeline の IItem を Scan → Search → 自動反映し、Save は setter と UndoRedo.Record を使う。これは JSON ファイル保存ではない。`RelinkMainWindow(string, ...)` は YMMP を Scan → Search → RelinkSaveService.SaveAsync する別経路だが現行コード上の呼出は確認できない。実ホスト経路の可用性は未確認。

JSON scan は JsonNode を探索し `Items` 以下の FilePath を行番号順に保持。検索はファイル名の大文字小文字を区別せず索引化し単一候補を自動選択、複数候補を曖昧とする。選択時に Updated 状態へ遷移し各行を維持する。旧保存は一致する FilePath 文字列を全文置換し固定 `.bak` を上書きしていた。新保存は行番号と元パスを JSON 値トークンに照合する。

Timeline の自動反映失敗は警告後に失敗内容を表示して中断する。特殊素材は事前拒否。通常の Timeline 操作途中に setter が失敗した場合の rollback は実装していない。
