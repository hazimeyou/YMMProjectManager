# ADR: Git 操作の信頼境界（R4 / 暫定）

状態: 採用する安全境界。リモート操作と未知の repository の実行境界は未完成、UI は無効。対象はデスクトップ利用、同一 Windows ユーザーの悪意あるプロセスからの完全隔離ではない。

- 利用者が選択した repo は自動的に安全とはみなさない。状態確認では `core.fsmonitor=false`。stage / switch は filter、commit は hook が実行される可能性を明示して承諾を要求。repo の hook を変更・無断削除しない。これは repo 内設定の外部実行を隔離するものではない。
- 未知の clone は設定・filter・template と credential helper の信頼を確立できるまで UI から実行しない。
- 自動試験は独立した temp repo と local bare remote、global / system Git config の抑止、合成データのみ。実ユーザー repo や credentials への push はしない。
- リモート操作は validation 後 URL と push 元 commit/ref を固定するが、Git 自身による URL rewrite、config include、credential helper、askpass、protocol helper、環境変数、repo 配置変更、同一ユーザーの競合を防いではいない。引数リストは shell injection 対策であり、Git 固有の外部実行に対する sandbox ではない。
- 独立レビューの合成実測: `origin` の `alpha→beta` と `beta→gamma` の rewrite 設定で、`remote get-url` は beta、続く URL 解決は gamma。ネットワーク接続なし。URL 文字列を固定するだけでは実効宛先を固定できない。
- 許可済みの通常 Git 認証と安全な unknown repo の制約を両立する実装・試験が必要。unsafe な一部操作を有効化するために credential helper 全体を恒久無効にする判断はしない。credentials を URL・ログ・平文保存しない。

操作ごとの信頼・helper・rewrite・repo identity を確定するまで clone / fetch / pull / push の UI は停止する。特定の操作だけ安全にできれば、その操作だけ有効化する。

## R14 の適用

Clone、Fetch、非強制 Push に操作ごとの明示承認を導入した。確認画面は接続先と、Push の場合はリポジトリのルート・フル ref・コミットを示す。承認前の Git コマンドで転送プロトコルと不足オブジェクトの自動取得を止める。承認後は選んだプロトコルに限る。URL 書き換えを検出した場合は停止する。利用者はインストール済みの Git、設定と認証用ヘルパーを信頼する必要がある。同一ユーザーの別プロセスの設定変更や OS レベルの共有パスアクセスは隔離の範囲外。Pull は ref・index・worktree の更新を一体として検証できるまで無効。
