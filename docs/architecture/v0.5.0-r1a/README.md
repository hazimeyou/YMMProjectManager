# R1-A プロジェクト一覧の保存保護

基準: `bdf6dbd32c910bd2a9ea6432399ba40f8f1d72af`（origin/master を 2026-11-14 に取得）。R0-A の再現 `0071dc906afd778aca71c759cfe5cc203b939160` を参照。対象は一覧 repository、呼び出し元、関連テストに限定する。

同じディレクトリに一意の一時ファイルを作成し、書込み・flush 後に既存ファイルを File.Replace する。保存例外は呼出元へ伝える。破損読込を空一覧へ変換せず、初期化前の保存を拒否する。Release build と既存・新規ランナーは成功。ACL と実 UI 等は未検証なので判定は PARTIAL。次の工程候補は世代 Manifest 保護。

- [保存設計](01-save-design.md)
- [失敗伝播](02-failure-propagation.md)
- [テスト結果](03-test-results.md)
- [制約](04-known-limitations.md)
