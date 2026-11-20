# ADR: R17 Pull の保全条件

状態 **読み取りの診断を拡張。既存 checkout を変更する Pull は BLOCKED**。

## 合成リポジトリで確認したこと

Git for Windows 2.55.0.windows.5 とローカル NTFS の一時領域を使用した。R16 で失敗した ref トランザクションを再実行せず、リンクした作業ツリーのロックと実際の HEAD ファイルハンドルを調べた。

| 方式 | 結果 | 判断 |
|---|---|---|
| linked worktree を `git worktree add --lock` で確保 | 管理情報に locked がある状態で、その作業ツリーの通常の枝切替は成功した。 | 管理情報を保持する機能。外部 Git からの作業ツリー更新を排他できない。 |
| 管理ディレクトリ内の実際の `HEAD` を Windows の読み取り共有のみで保持 | 同じ tree の枝切替は HEAD 更新を拒否した。異なる tree の checkout は終了コード 128 で拒否され、HEAD は旧版のままだったが、index と作業ファイルは新版に変更された。 | 実ファイルを固定しても、Git の実行全体は原子的にならない。partial publish を防ぐ条件として使えない。 |
| Windows の oplock と TxF | 仕様調査。ディレクトリで使える oplock のモードには書き込みを止めない通知があり、Git の複数対象を同時に公開する契約もない。TxF の名前付き操作には、操作側が transacted API を使用する必要がある。 | oplock は変更通知の材料。TxF は既存 Git 子プロセスをアプリ側からトランザクションに参加させる契約として使えない。 |

[Git worktree のロック](https://git-scm.com/docs/git-worktree) [Windows FSCTL_REQUEST_OPLOCK](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_request_oplock) [Transactional NTFS](https://learn.microsoft.com/en-us/windows/win32/fileio/how-to-use-transactional-ntfs)

## 無視対象の保全診断

合成リポジトリでは、無視されたローカルファイルに対して通常の porcelain が空を返した。明示的に `checkout --no-overwrite-ignore` を使うと終了コード 1 となり、ファイル SHA-256 と枝が維持された。R17 では、読み取り専用の fast-forward 候補に対して、Git が返す無視対象と追跡コミット間の追加・変更・種別変更パスを比較する。同一パスとディレクトリの上下関係を Windows の大文字小文字を区別しない比較で確認する。無関係な無視対象があっても候補の表示は継続する。二度の読取りで判定が変わった場合は再確認を要求する。この結果は観測時点の診断で、更新の承認や Windows 名前空間の全組合せの証明には用いない。

[Git checkout の無視対象の扱い](https://git-scm.com/docs/git-checkout) [Git tree 差分](https://git-scm.com/docs/git-diff-tree)

## 再開条件

既存 checkout の変更は、外部 Git も従う排他境界と、ref・index・作業ファイルの更新を中断後に検証・再開できる記録を実証した後に判定する。別領域に検証済みの候補を出力する設計は既存 checkout に触れずに試せるが、既存 checkout の Pull として扱うには別の製品判断を要する。 [R16 の比較](git-pull-recovery-r16.md)
