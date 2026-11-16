# R1-H2 Windows VM 隔離 — BLOCKED

実施日 2026-11-17。基準 R1-H `0e3d287ee034048b045c79e199509aed7e49a4ef`、親 R1-C `d2d46cb8f86ad51b508585760d96f8985e6c3628`、取得した origin/master `bdf6dbd32c910bd2a9ea6432399ba40f8f1d72af`。R1-C2 を merge していない。

ローカル fixed drive の空き約17.8 GiB、管理者 token は deny-only。Hyper-V 管理 module / VM 管理ツールは検出されず、機能状態照会は管理者権限不足。ハイパーバイザー検出は VM 管理基盤や VM 作成権限の証拠ではない。VM は未作成、guest・隔離・復元・YMM4 起動は未実施。明示承認を要する Windows 機能・ホスト設定は変更しない。Release build と合成 regression は実行した。

[ホスト](01-host-capability.md)・[設計](02-vm-design.md)・[構築](03-vm-build.md)・[隔離](04-isolation-evidence.md)・[復元](05-checkpoint-recovery.md)・[起動](06-ymm4-smoke-test.md)・[テスト](07-test-results.md)・[制約](08-known-limitations.md)
