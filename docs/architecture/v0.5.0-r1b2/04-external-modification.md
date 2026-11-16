# 外部変更

R1-B の合成 baseline では初回 load 後に有効 JSON の ProjectFileName を外部変更しても、元操作が成功して外部変更を失った。

修正後はロードで検証した実バイト列の SHA-256 と、temp の書込み完了後・置換直前の現ファイル SHA-256 を比較し、違えば InvalidDataException とログにより失敗を通知する。不正 JSON を含む変更も hash 不一致で拒否する。新規・欠落は hash null として存在変更を検出。

テスト hook で作成および削除の比較直前にそれぞれ異なる有効 JSON を外部書込みした。各操作は失敗し、外部変更と旧 snapshot を保持。削除は移動をロールバックした。比較終了から File.Replace / Move まで別プロセスに更新される窓（TOCTOU）は残り、完全な複数プロセス排他を保証しない。
