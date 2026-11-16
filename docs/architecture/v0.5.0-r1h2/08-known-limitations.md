# 未確認事項と再開条件

- 検出済み hypervisor は VM を操作できる証拠ではない。Hyper-V、Virtual Machine Platform、Windows Hypervisor Platform の状態、既存 VM と有効な管理経路は未確定。権限要求による読み取り失敗を無効と解釈しない。
- C: 空き約17.8 GiB は観測時点の値であり、他の固定 disk は検出されなかった。別の専用保存先・空き容量はユーザー確認が必要。容量不足を解決するための削除は行わない。
- Windows guest の使用権と OS 媒体は未確認。GPU 描画・UI Automation・.NET 10 / YMM4 guest 動作も未確認。
- 本番へのアクセス拒否、共有遮断、実効権限、checkpoint 復元は未検証。R1-A2 / R1-B / R1-C / R1-C2 の実ホスト受入は未完了。
- R1-C2 は別 branch、今回の基準には統合していない。安全条件・所定の承認が揃うまで H3 移行不可。
