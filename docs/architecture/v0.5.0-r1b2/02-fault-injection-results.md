# 障害注入

合成 project、正常な旧世代2件を持つ manifest、専用保存先を使用。内部の書込み delegate が temp に半分書き、容量不足相当 IOException または CancellationToken を発生。flush delegate は IOException を発生。既存 manifest を FileShare.Read で開いて実際の置換を拒否。各操作の結果・元の SHA-256 / サイズ / バイト列・旧 snapshot hash・無関係な固定 `.tmp` の hash・一意 temp の後始末を確認した。失敗作成の新規 snapshot は削除され、失敗削除の旧 snapshot は元の位置へ戻った。

最終実行の部分書込み時: Manifest 前後 `5C37AF178924DC618C83EFD19F9D7F07C4A33884D98AA6E3FAECB1BD699D0B18`、Snapshot 前後 `B3CC0475BB78A5026098858E9889ACF666D31062D513D303314ECA31D36E72F2`。Manifest hash はパスや日時を含むため再実行で変化する。削除失敗は `(false, error)`、作成失敗は例外を伝播。実ディスク容量を満たしていない。cleanup 自体の失敗注入は未実施で、後始末失敗時に temp が残る可能性がある。
