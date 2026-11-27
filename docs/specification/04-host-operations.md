# ホスト連携、画面、設定、運用

対象は基準HEAD `3a231161f2d103debdda0679c03af0bfe1fe56e8`。Windows 11 x64、.NET 10、YMM4 Lite 4.57.0.1、ローカルストレージがv0.5.0の正式対応方針。Windows 10、他のYMM4版、NAS等は正式保証の対象外。R34では候補ビルドと合成検証まで完了している。 [対応環境](../../README.md) · [R34検証](../development/v0.5.0-r34-results.md)

## YMM4への登録と起動

| 項目 | 実装上の仕様 |
| --- | --- |
| 起動入口 | `ToolPluginEntry` がYMM4の `IToolPlugin` を実装し、ツール名「プロジェクトマネージャー」、ViewModelの型 `ProjectListViewModel`、Viewの型 `ProjectListView` を公開する。コンストラクターから起動診断を初期化する。 [ToolPluginEntry](../../YMMProjectManager/Plugin/ToolPluginEntry.cs#L10) |
| 一覧の初期化 | ViewでDataContextが未設定ならViewModelを生成し、初回の `Loaded` で非同期初期化する。一覧が読めるとメモリー上の一覧、版、回復基準を確定してサムネイル情報を更新する。別処理のロックや中断記録がある場合は回復操作を案内する。 [ProjectListView](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml.cs#L12) · [初期化](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L368) |
| 現在のタイムライン | `ITimelineToolViewModel.SetTimelineToolInfo` で受け取った `TimelineToolInfo` を `TimelineContextService` が保持する。タイムライン向けのコマンド可否を更新する。 [ホスト情報の受取り](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L357) · [TimelineContextService](../../YMMProjectManager/Timeline/TimelineContextService.cs#L7) |
| 現在のPFパス | YMM4の非公開staticメソッド `ResourceDirectorySettings.GetProjectFilePath` をリフレクションで呼び出し、解決できないときは `null`。保存済みのPFパスを要求する操作は、未保存またはパスが未解決のときに中止する。 [YmmProjectPathResolver](../../YMMProjectManager/Host/YmmProjectPathResolver.cs#L7) · [開いているPFの同梱](../../YMMProjectManager/Ymmpx/UI/ProjectListYmmpxTransfer.cs#L19) |
| 処理中・取消 | ViewModelの `IsBusy` と各コマンドの `CanExecute` に従って操作を制御する。「処理を中止」は対応する操作のトークンを取り消す。一覧の変更・他プロセスのロックは専用の案内を表示し、未保存状態を保持する。 [コマンド初期化と処理ガード](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L252) · [処理ガード本体](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L895) |

## 画面と操作

| 画面・領域 | 操作と表示 |
| --- | --- |
| メインのプロジェクト一覧 | 「追加」「削除」「開く」と、一覧の行でのダブルクリック。ファイルのドラッグ＆ドロップは拡張子 `.ymmp` を大文字小文字を区別せず選別する。一覧の選択は `Projects` と `SelectedProject` に結び、名称、フルパス、サムネイルを表示する。 [一覧XAML](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml#L15) · [ドロップ・ダブルクリック](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml.cs#L41) |
| 一覧保存と競合 | 保存状態の表示と、再試行、ID設定・一覧変更の競合回復、退避一覧と画面状態の書き出し、中断された公開の確認、前回の保存記録、一覧原本の退避、ID確認・修復。専用競合画面は各選択を揃えると確認可能になる。 [回復コマンド](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml#L26) · [競合画面](../../YMMProjectManager/ProjectList/UI/ProjectListConflictReview.cs#L8) |
| 受け渡しと旧世代 | 「ymmpx展開」、一覧で選択したPFまたは現PFの同梱、除外設定取込み、状態表示、旧世代の別名取出し。YMMPXの確認画面で同梱・除外・欠落とUI情報の方針を確認する。旧世代は `metadata.json` を参照する。 [メインの入口](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml#L53) · [同梱確認](../../YMMProjectManager/Ymmpx/UI/ProjectListYmmpxTransfer.cs#L233) · [旧世代救済画面](../../YMMProjectManager/Generations/UI/ProjectListLegacyGenerationExport.cs#L13) |
| 「Git と素材」内のGit | ブランチ名の入力と作成・切替、状態、初期化、履歴、origin。保存済み資格情報の使用切替、アクセス診断、ブラウザー認証・再認証・中止。Fetch、Pull前確認、fast-forward Pull、Push、リモートと新規名を使ったClone。変更一覧、個別Stage・解除、コミットメッセージと確認付きCommit。 [Gitのバインディング](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml#L86) · [GitとYMMPXの仕様](03-git-ymmpx.md) |
| 「Git と素材」内の素材・ID | 候補検索、旧ID設定と旧メタデータ関連付け、選択PFの再リンク、素材パスの複数行入力、素材検査、メタデータ保存・新版作成・照合、配布元URLと許諾メモ、素材プール収集、外部素材照合、移動・改名の再関連付け、Project ID照合、旧履歴の取込み・版選択・履歴の移動。 [素材とIdentityのバインディング](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml#L140) · [プロジェクトとデータの仕様](02-project-data.md) |
| 素材再リンク | 種類、元パス、名前、拡張子、状態、候補数、候補選択、メッセージを表に表示する。探索と取消、更新内容の保存・中止、該当時の回復、閉じる操作を持つ。タイムラインと保存済みPFで検査・書込み契約が異なる。 [RelinkMainWindow](../../YMMProjectManager/Presentation/Relink/RelinkMainWindow.xaml#L10) |
| プロジェクト比較 | 元PF・現PFの選択、左右の表示領域、共通シーク・時刻入力、固定オフセット、区間の追加・変更・削除、設定の読み込み・別名保存、更新・中止・閉じる。公開入口の描画更新は無効。 [ProjectComparisonWindow](../../YMMProjectManager/Timeline/Comparison/UI/ProjectComparisonWindow.xaml#L15) |
| プロジェクトのサムネイル | 選択したPFの名称・状態、保存済み画像一覧、取得進捗、キャッシュ再読込、取得・更新、取消、閉じる。公開入口からは保存済みキャッシュの閲覧を提供する。 [ThumbnailGalleryWindow](../../YMMProjectManager/Thumbnails/UI/ThumbnailGalleryWindow.xaml#L10) |
| プロジェクトマネージャー設定 | 探索フォルダーの追加・削除・保存・再読込、使用容量の確認、完了キャッシュと過去ログの削除。削除には確認がある。 [設定画面](../../YMMProjectManager/Settings/YMMProjectManagerSettingsView.xaml#L8) |

動的なダイアログにはGit Push・Pull・Commitの確認、重複IDの残存項目選択、メタデータ履歴の版選択、YMMPX除外の取込み確認と同梱内容の確認がある。ファイル・フォルダーはWindows標準ダイアログで選ぶ。診断や動作が残るソースも、メインXAMLから到達する機能と区別する。 [Git確認画面](../../YMMProjectManager/Git/UI/ProjectListGitCommit.cs) · [Pull確認画面](../../YMMProjectManager/Git/UI/ProjectListGitPullWorkflow.cs#L127) · [ID修復](../../YMMProjectManager/Identity/UI/ProjectListIdentityRecovery.cs) · [版選択](../../YMMProjectManager/Identity/UI/ProjectListIdentity.cs#L181) · [YMMPX確認](../../YMMProjectManager/Ymmpx/UI/ProjectListYmmpxTransfer.cs)

## タイムライン、比較、プレビュー

### タイムラインへの直接反映

メインの「再リンク」はタイムライン情報がある場合に候補確認画面を開く。「選択プロジェクトを再リンク」は保存済みPF向けで、タイムラインを開いている間はファイル側の処理を止める。タイムライン側で更新を選ぶと、対象のホスト情報・タイムライン・項目実体・元パスと選択内容を再照合し、保持した候補最大10件の完全SHA-256を表示する。この経路では期待ハッシュとの一致判定と直接反映は無効。単一選択項目を置換する既存APIも警告を返す。実機での書込みを有効にするには、Undo/Redo、未保存編集、途中の失敗と回復、ファイルや項目の差し替えに対する契約の確認が必要。 [入口とガード](../../YMMProjectManager/Relinking/UI/ProjectListRelinkWindow.cs#L15) · [再照合とハッシュ表示](../../YMMProjectManager/Presentation/Relink/RelinkMainWindow.xaml.cs#L272) · [一覧書換えの拒否](../../YMMProjectManager/Relinking/TimelineMediaRelinkService.cs#L112) · [単一項目API](../../YMMProjectManager/Relinking/TimelineItemRelinkService.cs#L28) · [実機シナリオG](../development/v0.5.0-r34-scenarios.md)

### プロジェクト比較

- 元PFと現PFの選択、共通時刻、固定オフセット、区間ごとのオフセットを保持する。開始時刻が共通時刻以下で最も新しい区間を使用し、それより前は固定オフセットを使う。元PFの要求時刻は共通時刻、現PFは共通時刻にオフセットを加えた値。各側のFPSを分数で扱い、対象時刻を含むフレームに切り下げる。開始前、作品長以上、加算不能は範囲外として表示・結果を扱う。 [時刻仕様](../../YMMProjectManager/Timeline/Comparison/ComparisonTiming.cs#L33) · [フレーム換算](../../YMMProjectManager/Timeline/Comparison/PreviewFrameAlignment.cs#L62) · [表示と処理](../../YMMProjectManager/Timeline/Comparison/ProjectComparisonWorkspace.cs#L120)
- 比較設定はJSONの `Version: 1`、`DefaultOffsetTicks`、`Sections` 配列（`StartTicks`、`OffsetTicks`）。時刻単位は.NETのTimeSpan tick。入力上限64 KiB、最大512区間、開始は非負・重複なし・昇順。未知・重複プロパティと未対応版を拒否する。ローカルドライブ上の通常名を要求し、所有する一時ファイルから新規パスに公開する。PFパスや描画結果は保存対象に含まれない。 [形式・公開・読込](../../YMMProjectManager/Timeline/Comparison/ComparisonTiming.cs#L10) · [パス検査](../../YMMProjectManager/Timeline/Comparison/ComparisonFilePaths.cs#L8)
- 合成描画元を受け取る比較ワークスペースは、処理中に対象や時刻が変更された結果を破棄し、取消・終了時も表示を確定しない。内部のScene描画調停にはセッション、版、描画条件、要求IDと返却バッファ、編集状態の鮮度の照合、重複実行の拒否、期限と解放がある。画素比較は対応する `Bgra32`／`Pbgra32` のサイズ・形式・stride等を検査して差を計算する。公開画面の生成コードは実描画元を供給していない。独立Sceneとその寿命、実フレームと編集中状態の保全が実機接続の条件。 [公開画面の生成](../../YMMProjectManager/Timeline/Comparison/UI/ProjectComparisonWindow.xaml.cs#L18) · [Sceneの対応](../../YMMProjectManager/Timeline/Comparison/SceneComparisonPlan.cs#L5) · [描画調停](../../YMMProjectManager/Timeline/Comparison/SceneRenderCoordinator.cs#L45) · [画素比較](../../YMMProjectManager/Timeline/Comparison/PreviewFrameComparison.cs#L43) · [実機の観測条件](../development/v0.5.0-r32-host-evidence.md)

### 保存済みサムネイルと旧描画経路

- 一覧のサムネイルはホバー位置に応じた画像をキャッシュから選ぶ。ギャラリーは最大64点の `000.png`〜`063.png` を扱う。ギャラリーが表示を採用または破棄するまで照合したPFのハンドルと親を保持する。選択変更・パス変更・取消・閉じた後の結果は採用しない。 [一覧の画像操作](../../YMMProjectManager/Presentation/Controls/ScrubThumbnailControl.xaml.cs#L49) · [選択の有効性](../../YMMProjectManager/Thumbnails/UI/ProjectListPreview.cs#L21) · [ギャラリー](../../YMMProjectManager/Thumbnails/UI/ThumbnailGalleryWindow.xaml.cs#L58)
- キャッシュはYMM4ユーザーディレクトリの `plugin/YMMProjectManager/cache/filmstrip`。読込み優先順はPFパス＋物理ID＋完全SHA-256に結び付く現行の独立描画用、PFパス＋物理ID＋更新時刻に結び付く従来のライブ描画用、PFパス＋更新時刻の旧形式。現行の読込みでPFをハッシュする上限は64 MiB。所有記録がない旧キャッシュは「照合情報なし」と表示する。ギャラリーの画像は入力PNG1枚1 MiB・1,024画素辺まで、最大128画素辺へ縮小する。 [キャッシュの根](../../YMMProjectManager/Thumbnails/UI/ProjectListPreview.cs#L18) · [キャッシュ形式の選択](../../YMMProjectManager/Thumbnails/ThumbnailCachePublication.cs#L117) · [キー構成](../../YMMProjectManager/Thumbnails/FilmstripCacheKeyFactory.cs#L17) · [ギャラリー読込み](../../YMMProjectManager/Thumbnails/ThumbnailGalleryCache.cs#L14)
- 内部の独立描画契約は先頭から終端まで64スロットを抽出し、同じフレームを再利用する。対象PFの版と実際に取得したフレーム、凍結済み画像、描画元の寿命を確認し、解放後に完成キャッシュを公開する。上限はPF64 MiB、取得バッファ相当32 MiB。公開ギャラリー入口では描画元が未接続のため「取得・更新」を無効にしている。エディターのプレビュー取得、旧コピー・シーク・ベンチマーク・診断はソースに残るが、現行ギャラリーの新規描画仕様の根拠には含めない。 [独立描画](../../YMMProjectManager/Thumbnails/SeekPreviewThumbnailGenerator.Detached.cs#L10) · [スロット割当て](../../YMMProjectManager/Thumbnails/SeekPreviewThumbnailGenerator.cs#L165) · [ギャラリーの入口](../../YMMProjectManager/Thumbnails/UI/ProjectListPreview.cs#L21) · [診断アダプター](../../YMMProjectManager/Thumbnails/CurrentPreviewCaptureService.cs#L54)

## 設定、キャッシュ、ログの保存と保全

| 保存物 | 利用・保全の仕様 |
| --- | --- |
| 探索フォルダー設定 | プラグインDLLと同じディレクトリを起点に `settings/YMMProjectManagerSettings.json`。JSONは `SearchFolders` 配列。空の項目を除き、パス正規化後に大文字小文字を区別せず重複排除する。ファイル不在で退避記録がなければ空の設定を利用できる。読込エラー中は前回確認した値を保ち保存を止める。保存前にディスク版を物理IDとSHA-256で照合し、事前確認したファイルと一致するときに新しい値を公開する。 [設定の型とパス](../../YMMProjectManager/Settings/YMMProjectManagerSettings.cs#L11) · [読込・保存](../../YMMProjectManager/Settings/YMMProjectManagerSettings.cs#L88) |
| 中断した探索設定の回復 | 既存設定の公開時に `.previous-<操作ID>`、`.recovery-<操作ID>`、`.<操作ID>.tmp` を使用する。回復証拠v1は対象名と操作ID、親ディレクトリ・元設定・仮データの物理ID、双方のSHA-256を持つ。元の名前が空いており、単一の候補と証拠が一致する場合に、設定画面の再読込から明示確認付きで中断前の設定を戻せる。競合・証拠不一致・回復拒否では旧データを保全する。 [回復形式](../../YMMProjectManager/Settings/YMMProjectManagerSettings.Recovery.cs#L9) · [UIの確認](../../YMMProjectManager/Settings/YMMProjectManagerSettingsView.xaml.cs#L76) |
| サムネイルの完了キャッシュ | 作業中は `.capture-pending-<ID>`、公開後は `capture-<順序>-<ID>`。64点、`complete`、`ownership.json` を揃える。所有記録v1はディレクトリと画像の物理ID、画像名・長さ・SHA-256を記録する。256 MiBは所有確認済みキャッシュの整理に使う予算で、ディスクの使用量全体を制限する数値ではない。設定画面に確認済み、旧式・未照合、中断物、確認不能の区分を表示する。削除は確認済みの完了分に限定し、削除前に所有を再照合する。 [完成公開](../../YMMProjectManager/Thumbnails/ThumbnailCachePublication.cs#L10) · [所有証拠と整理](../../YMMProjectManager/Thumbnails/ThumbnailCacheMaintenance.cs#L10) · [使用容量と削除UI](../../YMMProjectManager/Settings/YMMProjectManagerSettingsView.xaml.cs#L111) |
| プラグイン診断ログ | DLLの隣の `logs/YMMProjectManager.log` にUTF-8で追記する。ログは時刻、レベル、検査済み呼出元名、例外型とHResultを記録し、自由文・例外本文・パス・資格情報は原則として永続化しない。AppDomain未処理例外、未監視タスク例外、Dispatcher例外を診断して、ロードコンテキストのアンロード時にフックとロガーを解除する。 [PluginDiagnostics](../../YMMProjectManager/Infrastructure/PluginDiagnostics.cs#L9) · [標準ログ出力](../../YMMProjectManager/Infrastructure/FileLogger.cs#L13) |
| ローテーション済みログ | 既定の1 MiB・7日でローテーションを判定。有効な所有証拠付きアーカイブを最大7世代、保存日数・総量で整理する。アーカイブは日時とGUID付きで、`.owner` 証拠v1に物理ID・長さ・SHA-256を持つ。設定画面の削除はこの証拠を確認した過去ログに限る。現行ログと証拠のない旧ログは保持される。 [ローテーション](../../YMMProjectManager/Infrastructure/FileLogger.cs#L88) · [集計と削除](../../YMMProjectManager/Infrastructure/FileLogger.cs#L149) |

プレビューPNGやシーク調査JSONを出力する開発・既存経路には、上記の通常ログとは別の一時領域がある。設定画面でのキャッシュ・ログ整理は、上表の条件で所有を確認したデータに適用する。 [プレビュー診断](../../YMMProjectManager/Thumbnails/CurrentPreviewCaptureService.cs#L27) · [長さ調査](../../YMMProjectManager/Thumbnails/TimelineDurationProbeService.cs#L21) · [シーク調査](../../YMMProjectManager/Thumbnails/UI/ProjectListPreview.cs#L69)

## ビルド、配備、配布

| 項目 | 現行の構成 |
| --- | --- |
| ランタイム・SDK | C#、WPF、`net10.0-windows` / x64。ホストから `YukkuriMovieMaker.Plugin.dll`、`YukkuriMovieMaker.Controls.dll`、`YukkuriMovieMaker.dll` を使用。製品側のビルドはこれらをコピーせず、テストプロジェクトの実行出力にはコピーする。YmmpxLibとGitは機能別依存。 [製品プロジェクト](../../YMMProjectManager/YMMProjectManager.csproj) · [試験プロジェクト](../../YMMProjectManager.Tests/YMMProjectManager.Tests.csproj) · [機能別依存](03-git-ymmpx.md) |
| 開発用参照 | `YMM4Path`、`YMM4_PATH`、`YMM4DirPath`、リポジトリ内 `libs/YMM4` の順で参照を解決する。プロジェクトは参照先にPlugin DLLがなければ `libs` 直下も確認する。公式Liteアーカイブと参照DLLのハッシュを照合するスクリプトがある。ビルドと合成回帰の参照先は隔離領域に明示する。 [MSBuild設定](../../Directory.Build.props) · [参照取得](../../scripts/fetch-ymm4-libs.ps1) |
| 開発配備 | `DeployToYMM4` を明示的に実行し、`YMM4DeployPath` のYMM4ディレクトリ内でPlugin DLLの存在を確認した後、`user/plugin/YMMProjectManager` に製品DLLをコピーする。開発用launchプロファイルの実行先は固定ローカルパスの例で、隔離環境を自動選択する設定はない。 [配備ターゲット](../../YMMProjectManager/YMMProjectManager.csproj#L25) · [launch構成](../../YMMProjectManager/Properties/launchSettings.json) |
| タグ配布の候補作成 | GitHub Actionsの `v*` タグ用ワークフローがタグとコミットを検証し、Windows runnerでRelease・Debugを独立検証する。SDK10.0.404とPython3.13.7、固定された参照と公式YmmpxLibを使う。全回帰の実ライブラリ有無、隔離Git HTTPS、実ライブラリの重点試験を実行し、対象DLL・テスト・依存とプロベナンスを照合する。 [配布ワークフロー](../../.github/workflows/release.yml) · [検証スクリプト](../../scripts/verify-release-build.ps1#L205) |
| 配布物と公開境界 | Releaseの `YMMProjectManager.ymme` はライセンス、README、deps.json、試験済みPM DLL、出力にあればPDBを収録。各エントリーを再読込してSHA-256を照合する。 `YMMProjectManager.build.json` v1にタグ、試験済みコミット、SDK、ハッシュ、参照、試験結果、CI実行IDを保存する。CIが作成するのはタグと成果物を照合済みのDraft prerelease。正式公開と手動受入は別工程。R34の単品候補は既定版0.4.0.0で、タグ版を反映する配布ビルドと識別できる。 [候補生成](../../scripts/verify-release-build.ps1#L231) · [Draft作成](../../.github/workflows/release.yml#L78) · [R34候補版](../development/v0.5.0-r34-results.md) |

## 試験・受入の状態

- `YMMProjectManager.Tests.exe` を合成データと隔離Git設定で実行する。クリーンな一時領域を構成別に分け、必要に応じて公式YmmpxLibや旧形式のfixtureを指定する。 `YMM_TEST_FILTER` は対象グループを選ぶ。Release・Debug × 実YmmpxLib有無はR34で終了コード0。ありは各307件成功、なしは各299件成功・条件付き9件スキップ、失敗0。全アサーションの総数ではなく個別計測の件数。 [試験入口](../../YMMProjectManager.Tests/Program.cs#L111) · [全構成の証拠](../development/v0.5.0-r34-results.md)
- WPFのサムネイル・比較、タイムライン候補確認、設定・キャッシュ、回復、描画調停に合成テストがある。実YMM4での画面・実描画・Timelineへの直接反映はR34で未受入。実GitHub認証も未受入。R34のVM依頼書は審査用で、管理側の候補ピン更新、VMと権限・隔離・回数・時間の承認が必要。 [比較](../../YMMProjectManager.Tests/PreviewComparisonTests.cs) · [旧キャッシュとサムネイル](../../YMMProjectManager.Tests/ThumbnailGalleryTests.cs) · [設定・診断](../../YMMProjectManager.Tests/SettingsAndDiagnosticsTests.cs) · [VM依頼書](../development/v0.5.0-r34-vm-request.md)
- R34 A01はホスト内表示を一覧読込みの開始・完了ログに結び付ける。管理側の既存 `R15Gui.HasProjectListLoad` は `Load start.` と `Load end. count=` の本文を探索する。現PMの `InitializeAsync` はこれらをログAPIへ渡し、`FileLogger` は呼出元名を記録する。R35で旧判定を再利用する前に、新候補について開始と完了を区別できる機械的な判定と合成試験を管理側で確認する。現時点ではコードと既存証跡の照合結果。 [一覧読込み](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L368) · [ログ形式](../../YMMProjectManager/Infrastructure/FileLogger.cs#L39) · [固定GUI手順と過去証跡](../development/v0.5.0-r34-manager-evidence.md) · [A01](../development/v0.5.0-r34-scenarios.md)
