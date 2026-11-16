# 分離の検証

観測: 実行中プロセスの Windows token は Medium Mandatory Level、BUILTIN Administrators は deny-only。`Get-Command Get-VM, VBoxManage, vmrun` ではコマンドが返らず、`Get-WindowsOptionalFeature` の Hyper-V 検査は管理者権限が必要として失敗。コマンド不在だけでハイパーバイザーが存在しないとは断定できない。現在の実効主体が本番データに書けないことは未確認。新しい VM、guest account、ACL、共有設定、registry を作成または変更していない。

本番の一覧・設定・世代・ログ・キャッシュ・素材に対する deny は **未成立**。書込み先と OS/YMM4 の動的処理に未確定箇所があり、実効権限・共有経路・復元を示す証拠がない。安全上、限定ホスト起動はしない。実際の生データに対する差分や hash を採取していないため、全本番ファイルが不変と証明したわけではない。
