# R1-A2 — 一覧保存の回復検証

判定 PARTIAL。基準 R1-A `c5f076595e0f3edf1461d2f8c588dbaaeb7ef0ac`（親 `bdf6dbd32c910bd2a9ea6432399ba40f8f1d72af`、2026-11-14 fetch origin/master は同親）。R1-A の worktree は変更しない。

独立 WPF の合成 fixture で警告ダイアログ表示と追加失敗後に同じ追加操作では保存されない問題を再現した。未保存表示と保存のみの再試行を追加し、独立テストで追加・削除・登録・キャンセルからの回復を検証。YMM4 実ホストと ACL は安全な隔離条件未確認で BLOCKED。実機受入は未完了。

- [保存呼出経路](01-save-call-paths.md)
- [通知](02-ui-notification-verification.md)
- [回復](03-recovery-verification.md)
- [ACL](04-acl-verification.md)
- [ホスト受入](05-host-acceptance.md)
- [テスト](06-test-results.md)
- [制約](07-known-limitations.md)
