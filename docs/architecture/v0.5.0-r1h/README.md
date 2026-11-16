# R1-H 隔離ホスト検証環境 — PARTIAL

実 YMM4 起動・プラグイン読込は **BLOCKED**。R1-C 基準 `d2d46cb8f86ad51b508585760d96f8985e6c3628`、origin/master `bdf6dbd32c910bd2a9ea6432399ba40f8f1d72af`（2026-11-17 fetch）。Run ID `r1h-20261117-01`。

専用 Windows VM を候補として設計したが、新規 VM・専用ユーザーの作成やホスト側設定変更は実施しておらず、既存データにアクセスできない実効境界も確認できない。別ディレクトリや環境変数だけでは分離成立ではない。参照用 DLL を一時領域へ複製した Release build と独立 test runner は正常終了、ホスト検証の代替ではない。本番コードは変更しない。

[方式](01-isolation-options.md)・[書込先](02-write-surface-inventory.md)・[設計](03-isolation-design.md)・[境界検証](04-isolation-verification.md)・[ホスト起動](05-host-smoke-test.md)・[テスト](06-test-results.md)・[復旧](07-recovery-procedure.md)
