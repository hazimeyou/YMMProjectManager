# VM 設計候補（未構築）

専用 Windows 11 VM、4 vCPU、RAM 8–12 GiB、仮想 disk 80–120 GiB を候補とする。Windows 11 VM の最低要件は [Microsoft の要件](https://learn.microsoft.com/en-us/windows/whats-new/windows-11-requirements) を参照。現在の C: 空き約17.8 GiB では guest の運用・checkpoint・host の余裕を確保できず、別の十分な空き領域を確認するまで作成しない。動的仮想ディスクも将来の実使用容量を消費する。

新規 VM 専用仮想ディスクのみ、既存 VM や既存仮想スイッチを変更しない。network は最初は未接続、shared folder / drive redirection / clipboard / USB / 本番 credentials は無効。Hyper-V Enhanced Session はクリップボード・デバイス共有を提供するため、[Microsoft の説明](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/enhanced-session-mode) を踏まえ設定と実効接続の両方を確認する。basic mode の選択だけで全経路が閉じたと判断しない。guest の GPU / UIA と VM checkpoint は動作未確認。現時点で方式・設定は提案であり実機構成ではない。
