# Git・GitHub・YMMPXの仕様

対象は `3a231161f2d103debdda0679c03af0bfe1fe56e8` の現行製品。過去の開発資料は当時の観測として参照し、現在の画面と処理を優先した。以下で「実装済み」はコード上の提供状態、「合成検証済み」は隔離した試験の観測、「実環境未検証」は実サービスまたは実YMM4用の受入が残る状態を示す。

## Git

### 操作と入出力

Gitはプロジェクトの変更履歴を管理する。プロジェクト一覧で対象PFを選び、「Git と素材」で操作する。CloneはURLと新規フォルダー名を入力し、親フォルダーを選ぶ。履歴・状態・失敗は画面に表示する。 [一覧のGit欄](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml#L87) · [ローカル操作](../../YMMProjectManager/Git/UI/ProjectListGitWorkingTree.cs#L15) · [リモート操作](../../YMMProjectManager/Git/UI/ProjectListGitRemote.cs#L13)

| 操作 | 利用者の指定 | 動作・結果 |
|---|---|---|
| Git 状態 | 一覧のPF | 対象PFの作業フォルダーからGitルートを確認し、変更一覧とブランチを表示。選択が変わった後に古い結果を表示しない |
| Git 初期化 | 一覧のPFと確認 | リポジトリが見つからない場合に確認し、状態を再確認してPFのフォルダーを初期化 |
| 履歴 | 一覧のPF | 最大100コミットの完全OID・作者名・作者日時・件名を表示 |
| origin | 一覧のPF | URLを検証して接続先を表示。取得できない場合はoriginを確認するよう案内 |
| ブランチ作成・切替 | ブランチ名 | 名前を検証する。切替は利用者が信頼したGitフィルター等が実行され得ることを明示し、無視ファイルを上書きしない切替を行う |
| ステージ・解除 | Gitルートからの相対パス。変更リストから選択可 | 指定パスのindexを更新。ステージ前に素材・私的データを検査する。解除は作業ファイルを残す |
| コミット | メッセージと承認 | ステージ済みの変更だけを対象に、ブランチ・親OID・tree・対象名を確認。承認内容と現在のindexが合う場合にrefを条件付き更新し、結果を表示する |
| Clone | HTTPS URLまたはローカル接続先、フォルダー名、親、承認 | 同じ親の所有ステージで取得し、新規フォルダーとして公開。既存先・危険な名前・検証できない接続先では停止する |
| Fetch | 一覧のPF、originと接続の承認 | 検証済み接続先から取得。作業ファイルのfast-forward適用はPullで行う |
| Pull 前の状態を確認 | 一覧のPF | 設定済みの追跡refと作業状態を読み取り、同一・候補・先行・分岐・未追跡等を分類 |
| Pull (fast-forward) | 一覧のPF、接続の承認、更新先OIDの承認 | 承認済みのコミットを既存の作業ツリーへfast-forwardで適用。安全条件は下記 |
| Push | 一覧のPF、送信先・ブランチ・OIDの承認 | 承認した単一ブランチの送信元OIDを固定し、公開対象を検査して通常Push。分岐などを分類して表示 |

Gitの作業データ・コミット・追跡refは利用者のリポジトリにある。PMが使う明示確定フローでは自動マージ・rebase・reset・force push・自動stashを提供しない。一般Git操作のタイムアウトは既定2分。Pullは準備・更新・結果照合に別の期限を設ける。リモートの生出力はそのまま表示せず、認証、権限、通信、リダイレクト、分岐、時間切れ、結果不明などに分類する。 [GitRepository](../../YMMProjectManager/Git/GitRepository.cs#L12) · [コミット確認](../../YMMProjectManager/Git/UI/ProjectListGitCommit.cs#L15) · [Git結果型](../../YMMProjectManager/Git/GitOperationResults.cs#L4)

### 信頼境界と認証

- 接続・送信は承認済みリポジトリとURLを照合する。リモート操作は通常の直接リポジトリ、明確なローカルパスまたは資格情報を含まないHTTPSを対象とする。URL書き換え、対応外のプロトコル、曖昧な出力、確認後の対象変更を拒否する。ローカルPushでは受信側の設定・hookが働くため、管理している接続先を選ぶ。SSHやGitHub以外のサービス固有認証は正式対応範囲外。 [リモート検証](../../YMMProjectManager/Git/GitRepository.cs#L76) · [接続承認画面](../../YMMProjectManager/Git/UI/ProjectListGitRemote.cs#L72)
- GitHubでは既定の匿名接続と、「GitHubにWindowsの保存済み資格情報を使う」の2モードを提供する。保存済みモードは検証したGitHub HTTPSのみに有効。機械管理下のGit for WindowsとGCMを保持して起動し、当該ホスト用の既存資格情報を読む。PM独自の資格情報ストアは持たない。「GitHubのアクセスを確認」は承認済みoriginを読めるかを確認する。公開リポジトリを読めた結果からログイン済みとは判定できない。 [GitHub接続処理](../../YMMProjectManager/Git/GitRepository.GitHub.cs#L78) · [アクセス確認](../../YMMProjectManager/Git/UI/ProjectListGitHubAccess.cs#L18)
- GitHubのGit通信はHTTPS、Schannelによる証明書検証、リダイレクト拒否を使用する。外部の資格情報ヘルパー、任意のGit設定・環境変数による接続を制限する。一般のローカル操作は利用者が信頼するGit設定を前提とする。外部エディタ・Gitクライアントとの完全排他や、すべての構成を隔離する契約はない。 [Gitプロセス設定](../../YMMProjectManager/Git/GitRepository.Process.cs#L67) · [GitHubのHTTPS設定](../../YMMProjectManager/Git/GitRepository.GitHub.cs#L185)
- 「GitHubにブラウザでログイン／再ログイン」は既存資格情報モードとは別に、公式GCM **3.0.2 Windows x64** の固定サイズ・SHA-256と管理下のACLを確認できた環境で提供する。通常ログインと再ログインを分け、取消と3分の期限を持つ。GCM用のローカルプロキシは `github.com:443` と `api.github.com:443` へのCONNECTに限定する。資格情報の保存はGCMが担当する。取消・通信拒否・時間切れで保存の成否が不明な場合は、アクセス確認から再開する。 [ブラウザ認証入口](../../YMMProjectManager/Git/UI/ProjectListGitHubBrowserLogin.cs#L11) · [実行ファイル照合](../../YMMProjectManager/Git/GitRepository.BrowserLogin.cs#L167) · [R24認証ADR](../architecture/adr/github-browser-auth-r24.md)

### Pullの更新・回復

1. 固定ローカルNTFS上の通常の直接チェックアウトを対象とする。現在branch、HEAD、index、作業ファイル、追跡先、既存作業記録を検査する。Git設定やチェックアウト内容を検証できない構成、未コミット変更、衝突する未追跡・ignoredファイル、外部filter、Windowsで曖昧なパス、リポジトリの変更を拒否する。
2. 利用者が接続先を承認してから実際のupstream OIDを確認する。さらに現在・更新先OIDを承認させ、取得後も追跡先、物理リポジトリ、現在状態、fast-forward関係を再照合する。Gitの `merge --ff-only` で承認済みOIDを適用し、branch ref・index・作業ファイルの結果を検証する。対象が同じOIDなら更新はない。
3. 書き込み前に `.git/ymmpm-pull-in-progress.json` を新規公開する。V1の記録は `Version`、`OperationId`、`State`、`CreatedUtc`、`BranchRef`、`StartObjectId`、`RemoteName`、`UpstreamRef`、`TargetObjectId`。終端を照合できた場合に当該記録を片付ける。結果不明なら作業状態と記録を維持し、同じrepoで次のPullを停止する。自動再適用や自動復旧は行わない。取得済みGitオブジェクトが停止後に残ることがある。

[Pullの確認画面](../../YMMProjectManager/Git/UI/ProjectListGitPullWorkflow.cs#L30) · [確定Pull](../../YMMProjectManager/Git/GitRepository.ConfirmedPull.cs#L55) · [Pull検査](../../YMMProjectManager/Git/GitRepository.PullSafety.cs#L34) · [作業記録](../../YMMProjectManager/Git/GitPullOperationJournal.cs#L49) · [R19 ADR](../architecture/adr/git-pull-confirmed-r19.md)

### Gitに載せるデータ

製品方針として素材本体、素材入りYMMPX、資格情報、回復記録などをGitで公開しない。素材を扱う場合は、共有できるハッシュ・参照・メタデータを使う。Stageでは指定ファイルと公開前の候補tree、Commitではステージ済みtree、Pushでは到達履歴中のパス・blobを検査する。プロジェクト、素材メタデータ、ファイルの内容・種類、既知素材のSHA-256、予約パスや既知の記録構造を照合する。素材の改名や最新コミットでの削除だけでは検査を免れない。

シンボリックリンク・gitlink・不明なtreeエントリ、取得できない履歴、Windowsで曖昧なパス、検査上限超過は拒否する。実装内の検査上限は一覧4 MiB、個別オブジェクト4 MiB、対象合計128 MiB。既知素材と検査が扱う形式が対象。未知の変換・外部のGit操作を含む全漏えい経路の保証はない。 [公開検査](../../YMMProjectManager/Git/GitRepository.Publication.cs#L15) · [既知素材の取込](../../YMMProjectManager/Git/UI/ProjectListGitCommit.cs#L98) · [Gitの公開境界と回帰](../development/v0.5.0-r33-nonvm-development.md)

## YMMPX

### 作成・展開・導入

| 操作 | 利用者の指定 | 動作・結果 |
|---|---|---|
| 選択した／現在のプロジェクトを同梱 | 一覧またはYMM4で開いている保存済みPF、許可する追加素材ルート、UI状態、確認済み対象、新規出力先 | 標準の `.ymmpx` を1ファイル作成。アプリ独自の新規許諾台帳やPM v1/v2用の受け渡し目録は作成しない |
| YMMPX 除外設定を取り込む | Packager設定JSON、PM全体または選択PFの取込先、反映承認 | 取込元と反映後の一覧をプレビューし、PM側にだけ保存 |
| ymmpx展開 | `.ymmpx`、新規展開先の既存親、承認 | ファイル名を使った新規フォルダーに展開し、素材参照を更新。PM旧目録の照合有無と判明した不足を表示 |
| 展開後に一覧へ追加 | 展開に成功したPFと登録の確認 | 一覧が初期化済みで未保存変更・保留中の回復がないときに提示。展開時の物理ID・SHA-256を再照合して新規登録。保存失敗時は展開済みファイルを保持 |
| YmmpxLibPluginの導入案内 | 公式配布物の新規保存先と明示承認 | 対応版の `.ymme` を検証して保存。ユーザーがYMM4の導入手順で開き、再起動する |

[作成と除外設定](../../YMMProjectManager/Ymmpx/UI/ProjectListYmmpxTransfer.cs#L18) · [展開と再登録](../../YMMProjectManager/Ymmpx/UI/ProjectListYmmpxImportExport.cs#L42) · [導入案内](../../YMMProjectManager/Ymmpx/YmmpxLibInstallGuide.cs#L46)

### 標準パッケージ作成

- YMMResourcePackagerの素材選定・除外形式を基準とする。PMの画面で、PFディレクトリと利用者が許可したローカル素材ルートの範囲から `FilePath` や `EnableLayersFilePath` の参照を調べる。同梱・除外・不足または読取不可、元素材名、元参照、サイズと完全SHA-256または利用不可理由を表示する。不足を含めた続行は明示確認を要する。
- 連番PNGは構成する素材ごとに個別に除外・照合する。PSDの対応参照を維持する。別ファイルの同名は区別し、一時ステージに検証済みの素材を採用する。入力PFの物理IDとハッシュ、素材の長さ・ハッシュ、確認済み除外設定を照合する。
- 公式 **YmmpxLibPlugin v1.1.0** の `YmmpxLib.dll` で `YmmpxPackageService.CreatePackageAsync` を呼ぶ。オプション・進捗・結果の型とシグネチャをリフレクションで検証する。ライブラリの結果を検証し、同梱されたPF・素材・参照・リンクマップを照合した上で、所有ステージの出力を非上書きで公開する。元PF・元素材は保持。
- 「プロジェクトのUI状態を同梱する」は既定オン。`IncludeProjectUiSettings` をライブラリに渡す。旧単独PF整理の機能はR32で撤去済みで、この選択は同梱するコピーに適用する。新規YMMPX作成は独自許諾入力を使わない。素材プールの任意の権利記録と、PM旧受け渡し目録の読取互換は別の契約。

[標準作成・検査](../../YMMProjectManager/Ymmpx/YmmpxStandardPackage.cs#L22) · [確認UI](../../YMMProjectManager/Ymmpx/UI/ProjectListYmmpxTransfer.cs#L229) · [ライブラリAPI](../../YMMProjectManager/Ymmpx/YmmpxLibApi.cs#L22) · [標準化・旧仕様撤去の経緯](../development/v0.5.0-r32-spec-integration.md)

### 除外設定・配布ライブラリ

| ファイル・境界 | 現行仕様 |
|---|---|
| PM全体の除外設定 | PMのアセンブリと同じフォルダーの `pm-ymmpx-exclude.json` |
| PF別の除外設定 | PFの隣の `<PFのベース名>.ymmpm-exclude.json` |
| 設定形式 | JSON配列。各行は `Path` または取込互換の `FilePath`、`IsFolder`（省略時は `false`）、`IsExcluded`（省略時は `true`）。共通にPF別ルールを合成し、同じパス・種類では後者優先。インポートは同じキーで取込元を優先。フォルダールールは配下も対象とし、別キーの有効な除外があれば適用。解析失敗時は停止 |
| 除外設定の上限と競合 | 1 MiB・最大1,000ルール。作成確認時の既存設定を物理IDとSHA-256で最終確認し、公開まで保持する。未作成だった設定も公開区間中は正規名を一時予約し、先行作成や更新途中に遭遇した場合は停止 |
| ライブラリ探索 | 明示指定、`YMMPX_LIB_PATH`、通常の導入先 `<YMM4のプラグインディレクトリ>/YmmpxLibPlugin/YmmpxLib.dll`。明示指定や環境変数の候補が不正な場合は導入先へ切り替えない |
| ロード条件 | 名称 `YmmpxLib`、AssemblyVersion `1.1.0.0` と公式DLLの完全SHA-256。通常導入先は `YmmpxLibPlugin.dll` の公式ハッシュも要求。確認したバイト列を専用AssemblyLoadContextでロードする |
| 公式インストーラー | `YmmpxLibPlugin v1.1.0`。承認後に検証対象の公式HTTPS配布先から取得し、31,051バイト・完全SHA-256を照合して新規保存。リダイレクトのホストと回数に制限がある。PMからの自動起動は終了済み |
| 旧ライブラリ | 旧 `YMMProjectManager/YmmpxLib` と `YmmpxLib` フォルダーを案内に表示し、既存内容を保持 |

[除外ルールと競合対策](../../YMMProjectManager/Ymmpx/YmmpxExclusionRules.cs#L9) · [R33の公開境界](../development/v0.5.0-r33-ymmpx.md) · [配布版と探索](../../YMMProjectManager/Ymmpx/YmmpxLibResolver.cs#L20) · [公式配布物と保存](../../YMMProjectManager/Ymmpx/YmmpxLibInstallGuide.cs#L10)

### 展開・互換性

| 形式 | 読み書きと検証 |
|---|---|
| 標準YMMPX | 新規作成・読取。ZIP内の `_ymmpx_project_path.txt`、対象 `.ymmp`、`links.json`、`resources/` を照合する。標準展開はPM側の安全なリーダーで処理し、YmmpxLibが未導入でも利用できる |
| 旧公開ライブラリ形式 | 読取。現行リーダーが検証可能な形式に限定する。名前から `resources/<同名>` に対応する旧リンクマップも厳格に照合。合成回帰で確認した版は `v0.2.1`、`v0.3.0`、`v1.0.1` |
| PM旧受け渡し v1 / 開発時v2 | 読取。旧形式の指標がある場合は同じディレクトリの `ymmpm-transfer.json` を必須とし、パッケージ名と完全SHA-256、素材の元名、サイズ、完全SHA-256、許諾状態、欠落参照を確認。v1はZIPコメントで目録と結び付け、v2はPF内の `YMMProjectManagerTransferV2` 証拠を検査する。v2は同名異内容の元素材名を別フォルダー経由で復元する |

旧目録のルートには `version`、`packageName`、`packageSha256`、`assets`、`missing` がある。`assets` の行は `archivePath`、`originalFileName`、`length`、`sha256`、`permission`。`missing` の行は `referencePath`、`originalFileName`、`length`、`sha256`、`permission` で、サイズとハッシュは対として省略できる。旧目録の欠落や改変を検出した場合、標準パッケージとして扱わずに停止する。 [旧目録の型と読込](../../YMMProjectManager/Ymmpx/YmmpxTransfer.cs#L31) · [旧形式検出](../../YMMProjectManager/Ymmpx/YmmpxPackageReader.cs#L119)

展開では既存先への上書きを拒否し、出力の親・所有ID・再解析ポイント、危険なエントリ名・重複・参照先・圧縮エントリを検査する。ステージを検査後に非上書きで公開する。取消・異常で所有が不明なファイルや出力は診断のため保持する。制限の例はPF JSON 64 MiB、リンク・旧目録16 MiB、マーカー4 KiB、展開ZIP 100 GiB・100,000エントリ。新規標準作成は素材最大9,997件・1点50 GiB・合計100 GiBの制限を持つ。

標準パッケージからPM旧目録相当の許諾・同一性を推定しない。公式API単体で作った標準アーカイブの未解決相対参照は、合成試験では保持された。PMの不足一覧に表示される条件は、旧目録やPMが作成時に付けた不足参照といった収録情報による。実YMM4への導入・再起動・実データの互換性受入は未検証。 [読取・展開](../../YMMProjectManager/Ymmpx/YmmpxPackageReader.cs#L93) · [エントリ検査](../../YMMProjectManager/Ymmpx/YmmpxLibBridge.cs#L274) · [旧目録スキーマ](../../YMMProjectManager/Ymmpx/YmmpxTransfer.cs#L16) · [作成上限](../../YMMProjectManager/Ymmpx/YmmpxStandardPackage.cs#L15)

## 検証記録と残る確認

| 対象 | 現時点の証拠 | 受入・仕様確認 |
|---|---|---|
| Git ローカル・Pull・公開検査 | 合成リポジトリでbranch、index、ファイル、競合・中断、送信拒否を回帰。隔離HTTPSは実Gitとループバックの試験サーバーで検証 | 実GitHubと本物のWindows資格情報、公開・非公開リポジトリの一連のユーザー操作は別途受入 |
| ブラウザ認証 | 対応インストールの検査、取消、プロキシ宛先、通信拒否を合成検証 | 対応GCMで実ブラウザ・実アカウント・資格情報の保存と再利用を別途受入 |
| YMMPX標準・旧形式 | 実YmmpxLibあり／なし、標準・旧公開版・PM旧v1/v2、除外・欠落・非上書き・境界を隔離回帰。R34で標準・旧3形式を候補で合成展開 | 実YMM4での操作と素材参照は別途受入。ライブラリの後続版は対応ピン、API、出力互換性の確認が必要 |

[該当テストの登録](../../YMMProjectManager.Tests/Program.cs#L204) · [隔離HTTPS](../development/v0.5.0-r20-https-acceptance.md) · [R34回帰・受入境界](../development/v0.5.0-r34-results.md)
