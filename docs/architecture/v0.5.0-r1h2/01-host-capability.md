# ホスト能力（読み取り専用、2026-11-17）

| 項目 | 観測・制約 |
|---|---|
| Windows | Windows 11 Pro、10.0.26300、x64 |
| CPU | Intel Core i7-12700F、16 logical processors。CIM の 8 cores は環境からの報告値 |
| Memory | 約63.8 GiB 可視、約30.2 GiB 空き（調査時点） |
| Disk | ローカル fixed drive は C: のみ、約930.5 GiB 中約17.8 GiB 空き（時点依存）。ユーザー指定の別保存先は未確認 |
| GPU | NVIDIA GeForce RTX 2060、Parsec Virtual Display Adapter を検出。VM ゲストへの GPU 利用可否は未確認 |
| Hypervisor | `HypervisorPresent=True`、systeminfo は既存 hypervisor を検出し詳細要件を表示しない。VBS running。CIM の CPU virtualization 欄は false を返したが、hypervisor 存在下の値を BIOS 無効の根拠にしない |
| 管理 | 実行 token Medium、Administrators deny-only。Hyper-V optional feature の照会は管理者権限不足。`Get-VM`、VBoxManage、vmrun、QEMU command、代表的既定インストールパスは検出できず。Hyper-V WMI namespace は無効。これだけで全仮想化製品や既存 VM の不存在は証明しない |

Windows 11 VM の要件と Hyper-V 管理・構築には [Microsoft の Windows 11 要件](https://learn.microsoft.com/en-us/windows/whats-new/windows-11-requirements) と [Hyper-V インストール手順](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/install-hyper-v?pivots=windows) を参照。Windows guest の使用権・ライセンスは所有する契約に依存するためユーザー確認が必要。[Microsoft の商用仮想デスクトップ案内](https://www.microsoft.com/licensing/guidance/Windows-11-Licensing-for-Virtual-Desktops) は商用向けであり個別の権利を断定しない。
