# 制約

- 現行主画面からの Timeline 再リンクは JSON ファイル経路と別。実 YMM4 ホストでの変更・保存・undo・UI通知と失敗途中の部分更新は未検証。実 UI の同一元パス別候補選択も未確認。
- PSD、VideoItem の PNG は安全側で拒否し、既存機能の完全な互換性は達成していない。その他、未知の YMM4 素材形式が FilePath 以外の関連プロパティを持つ可能性は未調査。
- Items の配列要素直下以外の FilePath、UTF-16、重複プロパティ、標準外 JSON を拒否する。実データの形式分布は未確認。
- 内容検査後から File.Replace までの TOCTOU と別プロセス競合は残る。パスごとの semaphore はプロセス中に保持される。alias / symlink 経路は同一と認識しない場合がある。
- ファイル置換と flush は power loss / storage 障害に対する完全な耐久性を保証しない。backup や temp の後始末が失敗するとファイルが残り得る。
- 実ホスト E2E、実素材、長パスの実ファイル、同名探索、旧 PSD / PNG branch との統合は未実施。R1-A2 の UI / ACL、R1-B2 の競合窓も継続して未完了。
