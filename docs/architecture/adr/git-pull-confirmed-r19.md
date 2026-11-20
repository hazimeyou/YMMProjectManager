# ADR R19 — 確認付き fast-forward Pull

R19 の製品判断は、利用者が承認した既存 checkout に対する fast-forward 更新を許可する。既知の消失条件を防止・検出し、更新結果を実際の ref、index、作業ツリーから判定する。協調しない外部 Git を完全に排他することは受入条件から外れた。承認は外部変更の上書きや自動巻き戻しを許可しない。 [R18 の判断と実測](git-pull-recovery-r18.md)

## 比較した実装

| 方式 | 判断 |
|---|---|
| `git pull --ff-only` | Git が upstream の解決と Fetch をまとめて実行するため、その処理に入る前に利用者が確認した対象コミットへ結び付けにくい。 |
| 承認した ref の Fetch と完全な OID を渡す `git merge --ff-only --no-autostash --no-overwrite-ignore` | 採用。承認の対象と適用先を再確認できる。Git の通常の作業ツリー更新を使い、非 fast-forward、既知のファイル衝突、自動 stash を拒否する。 |
| ref の条件付き更新と独自の作業ツリー展開 | ref の更新と index・作業ツリー公開が分かれる。中断時の復旧対象が増え、無視ファイルや外部ライターの扱いを独自に実装する必要がある。 |
| 別パスへの候補公開 | R18 の比較・復旧資料として保持する。既存 checkout の Pull として公開しない。 |

Git の `merge` は既定で ignored ファイルを上書きできるため、作業ツリーの事前検査と実行オプションの両方で衝突を扱う。Fetch は対象 ref を問い合わせ、承認された OID を適用前にも照合する。ネットワーク側が更新結果の確認直後に再び動く可能性は残る。 [Git merge](https://git-scm.com/docs/git-merge) [Git fetch](https://git-scm.com/docs/git-fetch)

## 境界

| 区分 | 対象 |
|---|---|
| 防止する | 同一プラグインプロセスからの重複 Git 書き込み。未承認の更新。自動 stash、rebase、force。既存の Pull 操作記録の上書き。 |
| 検出して停止する | 承認後のリポジトリ・branch・HEAD・upstream・対象コミットの変更。未コミット変更、競合する未追跡・無視ファイル、Git の進行中操作やロック。Windows の大小文字の別名、reparse point を経由する書き込み先、外部 checkout filter を要するツリー。 |
| 回復の手掛かりを残す | 実行前に直接の Git ディレクトリへ照合用の操作記録を公開する。中断や照合不能で結果が不明な場合は記録を保持する。 |
| 残存リスク | 外部 Git・エディタはプラグインのゲートを共有しない。検査と更新間の変更や、2回の観測間に元へ戻った変更は観測できない場合がある。電源断後のストレージ状態は未検証。 |
| 対象外 | 固定ローカル NTFS の通常の直接 checkout 以外、gitfile、linked worktree、検査できない設定や履歴、通常のファイル共有契約を満たさないパス、認証やリダイレクト、プロキシを必要とする接続。 |

ネイティブの子プロセスには固定したローカル config と制限付きの実行設定を用いる。外部 hook、fsmonitor、credential helper、暗黙のサブモジュール・lazy fetch を抑止する。確認できる改行設定は引き継ぐ。外部フィルターを呼び出す代わりに、承認済みコミットの `filter` 属性を `git check-attr --source` で調べて拒否する。読み取り Pull 診断も worktree の status 観測ごとに制限設定とローカル config ハンドルを使う。 [Git check-attr](https://git-scm.com/docs/git-check-attr)

確認済みの操作記録だけを処理する。結果が不明なときは、対象リポジトリのほかの操作を止め、現在の branch・HEAD、index、未コミット・未追跡・ignored ファイル、Git のロックと残った操作記録を確認する。自動 reset、クリーンアップ、再適用は行わない。
