# プロジェクト一覧・履歴・素材の仕様

対象は `codex/ymmpm-v050-r34-vm-acceptance-preparation` の `3a231161f2d103debdda0679c03af0bfe1fe56e8`。開発中の仕様を製品ソース、設計記録、隔離テストから整理した。実機受入の状況はR34の結果を参照。 [設計思想](../architecture/DESIGN_PHILOSOPHY.md) · [R34の検証記録](../development/v0.5.0-r34-results.md)

## プロジェクト一覧

| 操作 | 入力と結果 | 停止条件・制約 |
| --- | --- | --- |
| 起動・読込 | プロジェクトマネージャーの画面を初めて表示したときに、登録一覧とフォルダー情報を読む。登録行の表示名、パスとサムネイルを表示し、選択行を各操作に渡す。 | 別処理が一覧を使用中、または中断された公開がある場合は案内を表示する。読込不能なら保存を伴う機能を進めない。 |
| 追加 | ファイル選択ダイアログで複数選択、またはファイルをドロップする。存在する `.ymmp` を絶対パスで登録し、新しいGUIDを付け、主ファイルのリンクを用意する。 | 既存登録と同じパスを飛ばす。証拠が採れる場合は同じ物理ファイルの別パスでの登録も拒否する。証拠が採れない登録の自動回復には制約がある。 |
| 削除 | 選択行を一覧から除き、選択を解除して一覧を保存する。 | 操作対象は登録行。元PF、素材、履歴は保持する。 |
| 開く | 「開く」または行をダブルクリックする。選択PFを検証してWindowsの関連付けで開き、最終アクセス日時を保存する。 | 開けないパスは理由を表示する。保存済みPFを対象にする。 |
| 並び・リンク | 一覧は登録の順序を保存する。データモデルは表示名、ピン、フォルダー、関連YMMPの役割・メモも保持し、保存と回復で扱う。 | 現行の一覧画面で公開される主操作は追加・削除・開く。データモデルにあるフィールドを編集できる専用UIが揃っているとは扱わない。 |

根拠は [一覧の画面](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml#L20)、[ドラッグとダブルクリック](../../YMMProjectManager/Presentation/Views/ProjectListView.xaml.cs#L42)、[読込・登録・削除・起動処理](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L368)、[登録データの型](../../YMMProjectManager/ProjectList/ProjectItem.cs#L11)。

### 保存ファイルと互換性

| 対象 | 形式・場所 |
| --- | --- |
| 現用一覧 | `YMM4DirPath` 設定時は `<YMM4DirPath>/user/plugin/YMMProjectManager/data/projects.json`。未設定時は `%APPDATA%/YMMProjectManager/plugin/YMMProjectManager/data/projects.json`。 |
| ルート | JSONオブジェクト。 `Projects`、`Folders`、任意の `SaveOperationId`。一覧本文のSHA-256と操作IDは読み込んだ版の照合にも使う。 |
| プロジェクト行 | `FullPath`、`ProjectId`、`MetadataAssociation`、`FileIdentity`、`DisplayName`、`Pinned`、`LastAccess`、`FolderId`、`LinkedYmmpFiles`。 |
| リンク行 | `Id`、`FilePath`、`DisplayName`、`Role`、`Memo`、`RegisteredAt`、`UpdatedAt`、`LastCheckedAt`、`Exists`。役割は `Main`、`Backup`、`Draft`、`Branch`、`Short`、`Archive`、`Rejected`、`Unknown`。 |
| フォルダー行 | `Id`、`Name`、`DisplayOrder`、`CreatedAt`、`UpdatedAt`。 |

UTF-8 BOM付きの保存済み一覧も読み込む。旧項目のProject ID欠落を許容する。旧リンクのIDは項目パス・リンクパス・出現順に基づいて補い、登録PFのリンクが空なら主リンクを補う。空または重複した明示Project IDは読込を拒否する。ルートや入れ子の未知フィールドは、安定したIDなどで安全に対応付けられる場合に再保存する。破損・重複や曖昧なフィールドで安全な再保存ができない場合は、原本や候補の別名退避へ誘導する。 [読込と旧形式](../../YMMProjectManager/ProjectList/JsonProjectRepository.cs#L132) · [未知フィールドの保存条件](../../YMMProjectManager/ProjectList/JsonProjectRepository.cs#L212) · [保存DTOとパス](../../YMMProjectManager/ProjectList/JsonProjectRepository.cs#L780)

### 保存・競合・中断回復

1. 一覧編集は、読み込んだ版と保存候補を基に進める。回復可能な基準版がある場合は、一覧の変更を保存前に回復用下書きへ記録する。証拠を作れなかった追加には再適用の制約がある。 [下書きと保存の連携](../../YMMProjectManager/ProjectList/UI/ProjectListDraftRecovery.cs#L33)
2. 書込みはプロセス間ロックを取り、固定ローカルNTFS上の通常ファイルを対象にする。候補に全内容を書いてフラッシュし、保存予定のSHA-256を照合する。確認した既存版を所有するファイルハンドルから一時退避し、照合した候補だけを空いた現用名へ公開する。外部プロセスが新しい現用ファイルを作った場合は、そのファイルを保存候補で置き換えず、作業証拠を保持して停止する。初回は新規公開のみ。現用名が一時的に空く区間がある。 [保存処理](../../YMMProjectManager/ProjectList/JsonProjectRepository.cs#L556) · [一覧の置換と回復の設計](../architecture/adr/project-list-publication-r17.md)
3. 保存中に別の変更を検出したら保存済みとは扱わない。「一覧の保存を再試行」「ID 設定の競合を回復」「一覧変更の競合を確認」から状況に合う操作を選ぶ。一般の競合は読込時の版、画面上の変更、最新のディスク版を比較する。安全に両立する変更を統合し、同じ項目・属性への競合や順序などの判断は確認画面に出す。新規・移動先などのファイルは再照合し、追跡できない変更は退避する。 [競合の処理](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L680) · [三方向比較](../../YMMProjectManager/ProjectList/ProjectListMerge.cs#L12) · [回復ファイルの照合](../../YMMProjectManager/ProjectList/ProjectListRecoveryFiles.cs#L15)
4. 「中断された一覧保存を確認」は現用・退避・候補と操作証拠を検査して明示的に回復する。空いた現用名へ確認済みの版を戻すか、現用版を維持する。外部版を維持・復旧した場合は古い下書きの自動再適用を防ぐ。破損、差替え、不明な実行結果では証拠を保持する。 [公開回復の画面](../../YMMProjectManager/ProjectList/UI/ProjectListPublicationRecovery.cs#L27) · [置換の状態判定](../../YMMProjectManager/ProjectList/ProjectListReplacement.cs#L74)
5. 「前回の保存記録を確認」で下書きとレシートを選択する。公開済みと照合できれば適用済みと表示する。未公開と確定できる場合は最新一覧との比較へ進む。成否不明、閉鎖済み、新しい世代がある記録は画面から現用へ再適用せず、別ファイルの候補として退避できる。「退避一覧を保存」「画面状態を退避」「一覧原本を退避」も現用ファイルとは異なる保存先を使う。 [下書きの画面](../../YMMProjectManager/ProjectList/UI/ProjectListDraftRecovery.cs#L92) · [画面と候補の退避](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L807)

| 回復用データ | 内容 |
| --- | --- |
| `projects.json.recovery` の下書き | `*.draft.json`、処理中の `*.saving`、終了済みの `*.closed`。v1/v2を読み、基準版・編集版のバイト列、追加PFのIDとSHA-256、後続世代の情報をチェックサム付きJSONに保持する。 |
| 同フォルダーの公開レシート | `*.publication.json`、準備用 `*.publication-pending.json`、封印用 `*.publication-sealed.json`。読み込みはv1〜v3。v3は候補の物理IDとSHA-256も記録する。 |
| 一覧の親フォルダー | `projects.json.publication-pending`、`projects.json.parked-<操作ID>`、`projects.json.tmp-<操作ID>`。v1の置換記録が元版と結果版を結び付ける。回復結果を `projects.json.publication-<操作ID>.closed` または `.closed-external` に残す場合がある。 |

回復用ファイルは個別に手動削除すると照合に必要な証拠を失うことがある。電源断、ファイルシステム障害、証拠を同権限の別プロセスが消す場合は受入未実施。 [下書きの形式](../../YMMProjectManager/ProjectList/ProjectListDrafts.cs#L14) · [レシートの形式](../../YMMProjectManager/ProjectList/ProjectListPublications.cs#L14) · [置換記録の形式](../../YMMProjectManager/ProjectList/ProjectListReplacement.cs#L19)

## Project Identity・履歴

### 論理IDとファイルの照合

- **Project ID** は一覧で維持する論理GUID。**物理ID** は `Volume` と `FileId` の組。読み込んだ内容は完全なSHA-256で特定する。IDを照合する画面では、同じファイルで内容だけが変わった場合と、物理ファイルが変わった場合を区別し、再確認後に記録を更新する。 [物理ファイルの証拠](../../YMMProjectManager/Identity/ProjectFileEvidence.cs#L29) · [識別情報の確認](../../YMMProjectManager/Identity/UI/ProjectListIdentity.cs#L113)
- **旧項目** は「旧項目に ID を設定」で新しいIDを付けるか、「旧 metadata を関連付け」で利用者が選んだ旧メタデータのIDを明示的に関連付ける。確認後に現物と一覧を再照合する。旧項目にIDがない場合でも、関連付けの選択肢とID発行は別操作。 [旧項目の操作](../../YMMProjectManager/ProjectList/UI/ProjectListViewModel.cs#L580)
- **重複修復** は、物理的に同じファイルを指す登録の衝突を先に調べ、続いて重複した論理IDや旧項目を確認する。残す登録・ID継承先を選び、原本を新規ファイルに退避して修復を反映するか、修復済み一覧だけを別名保存する。退避・出力先の既存ファイルを上書きしない。 [重複確認・修復画面](../../YMMProjectManager/Identity/UI/ProjectListIdentityRecovery.cs#L16)
- **移動・改名** は旧パスの不在が確認できるときに、新しい `.ymmp` を選び同じ論理IDを使うことを承認する。移動先と既存登録を再照合して一覧と該当リンクだけを更新する。元が残るコピーは「追加」から独立登録する。相対素材パスの解決結果は移動後に要照合。 [移動・改名](../../YMMProjectManager/Identity/UI/ProjectListIdentity.cs#L341)

### 素材メタデータの版管理

| 操作・保存形態 | 仕様 |
| --- | --- |
| メタデータ保存・照合 | 検査済みの素材についてJSON版を新規保存する。「メタデータ照合」は選択した記録のProject IDまたはファイル名を確認してから差異を表示する。名前だけで照合した場合はID関連付け未確認と表示する。 |
| 新しい版 | 素材の追加・更新を確認して履歴へ版を追記する。サイズ・ハッシュの変更は明示承認が要る。既存版は保持する。追加した版を使うには「履歴の版を選択」で選び直せる。 |
| 旧版の取り込み | 選択した旧JSON版を新しい `<PF名>-asset-history` フォルダーの系列へ取り込む。ファイルの読み込み上限、親子関係、Project ID、重複、公開先の衝突を検証する。 |
| 版の選択と移動 | ルートと版の証拠を照合し、枝が複数あれば一覧を示す。関連付けた履歴の移動先には同じルートと版の証拠を要求する。選択版と観測した先端の相違は表示・検証する。 |
| 素材の版JSON | `SchemaVersion=1`、`ProjectId`、`ProjectFileName`、`RevisionId`、`ParentRevisionId`、`VerifiedAtUtc`、`Assets`、任意の `DerivedFrom`。系列は `revision-<GUID>.json` と `.asset-revisions.lock`。上限は版4 MiB、系列64 MiB・4096版。 |
| 一覧との関連付け | `MetadataAssociation` に `Directory`、`RootRevisionId/RootSha256`、`SelectedRevisionId/SelectedSha256`、`ObservedHeadRevisionId/ObservedHeadSha256` を持つ。 |

系列の版追記はルート、選択した枝の先端、観測した先端集合を照合する。別の追加が先行した場合は再確認が必要になる。同じ版IDの別内容を受け入れない。派生系列のルートは元Project ID、元系列のルート・版のIDとハッシュ、元PFのSHA-256を保持する。 [版JSON](../../YMMProjectManager/Assets/AssetManifestStore.cs#L8) · [系列と版追記](../../YMMProjectManager/Assets/AssetRevisionSeries.cs#L16) · [版保存と照合の画面](../../YMMProjectManager/Assets/UI/ProjectListAssetInventory.cs#L83) · [系列の選択・移動・照合](../../YMMProjectManager/Identity/UI/ProjectListIdentity.cs#L145)

### 旧世代のPF救済

「旧世代からPFを取り出す」で、通常は `%APPDATA%/YMMProjectManager/Generations/projects/<旧ID>/generations/<世代ID>/metadata.json` または同じ旧ID下の `deleted/<世代ID>_<時刻>/metadata.json` を選ぶ。同フォルダーの `project.ymmp` とメタデータの `GenerationId`、完全パスの `SourceProjectPath`、`FileSize`、`Sha256` が対象。メタデータは4 MiB以下。確認後に両ファイルの物理IDを再照合し、保存したコピーのサイズと完全SHA-256が一致してから別名PFを公開する。元PF、目録、世代データを保持する。出力が元PFと同名、旧世代保管先の配下、既存ファイルの場合は拒否する。現行の世代データ向け公開操作はこの救済であり、新規スナップショット作成・削除は旧機能。別フォルダーに出したPFの相対参照は出力先で確認する。 [救済画面](../../YMMProjectManager/Generations/UI/ProjectListLegacyGenerationExport.cs#L13) · [救済の検証と公開](../../YMMProjectManager/Generations/LegacyGenerationExport.cs#L16) · [旧世代救済ADR](../architecture/adr/legacy-generation-export-r32.md)

## 素材検査・プール

| 操作 | 仕様 |
| --- | --- |
| 素材候補 | 選択した保存済みPFに含まれる `FilePath` を調べる。PFは32 MiB以下、参照は1,000件以下。ファイルあり、不足、変更、プロジェクト外、検査不能を表示し、既知の版との照合に使える。 |
| 素材検査 | プロジェクト配下の相対パスを1〜1000件入力し、ファイルのサイズと完全なSHA-256を取得する。入手先URLは1件指定時だけ登録できる。 |
| 外部素材を検査 | 利用者が指定した保管場所と相対パスから検査する。保存する外部素材の記録は保管場所の表示名と保管ルートからの相対パス。対象のルートは利用者が確認時に指定する。 |
| 登録済み素材をまとめて照合 | 選択した版の登録をもとに、処理ごとに保管先名とそのルートを指定して該当する素材を照合する。一致・不足・サイズ差・SHA-256差・未確認・読取不能を区別する。 |
| 素材プールに収集 | 参照1件、元ファイル、既存の保管フォルダーを選ぶ。PFと参照が現在のGitコミットに含まれることを確認し、元のファイル名と選択ファイル名を照合する。コミット・対象・元参照を承認時と再照合したうえで、配布用コピーと目録をプールへ保存する。 |

素材メタデータの基本フィールドは `RelativePath`、`Size`、`Sha256`、`DownloadUrl`。外部保管用の `ExternalLocation` は `StorageName` と `RelativePath`、派生再リンク用の `ExternalRelink` は `OriginalFileName`、`ReferenceSha256`、任意の `ManifestSha256` を持つ。相対パスは承認されたルートの内部に制限する。入手先URLは案内情報として持ち、プラグインは素材を取得・公開しない。 [素材モデルとパス検査](../../YMMProjectManager/Assets/AssetInventory.cs#L11) · [素材検査の画面](../../YMMProjectManager/Assets/UI/ProjectListAssetInventory.cs#L18) · [外部素材の照合](../../YMMProjectManager/Identity/UI/ProjectListIdentity.cs#L39)

プールの1記録は `asset-pool.json`（スキーマ1）と配布ファイルからなる。目録フィールドは `SchemaVersion`、`OriginalFileName`、`DistributionFileName`、`Sha256`、`Size`、`OriginalReference`、`CommitId`、`DownloadUrl`、`RedistributionNote`。配布名の短縮ハッシュは16桁で、内容の照合には64桁の完全SHA-256を使う。目録全体のSHA-256も格納名に含めるため、同名異内容と同内容の異なる使用記録を区別できる。既存の同一記録は照合して再利用し、破損や競合を上書きしない。目録は32 KiB以下。1ルートの候補フォルダーが512件を超えたときは目録の明示指定を求める。通常のUNCパスを扱うコードも存在し、実NASは正式受入の対象外。配布は利用者がファイルを手動で渡す。 [収集の画面](../../YMMProjectManager/Assets/Pool/UI/ProjectListAssetPool.cs#L14) · [素材プールの形式・検証](../../YMMProjectManager/Assets/Pool/AssetPool.cs#L10) · [素材プールの運用](../development/v0.5.0-r21-asset-pool.md)

## 厳格な素材再リンク

1. 一覧でPFを選択して「選択プロジェクトを再リンク」を開く。編集中のTimelineがあるときは保存済みPFを操作する経路を停止する。対象PFと履歴の関連付けを確認し、検索フォルダーや `asset-pool.json` を利用者が指定する。 [対象PFと起動条件](../../YMMProjectManager/Relinking/UI/ProjectListRelinkWindow.cs#L14) · [検索入力](../../YMMProjectManager/Relinking/UI/RelinkStrictSearch.cs#L21)
2. 期待値が登録された参照について、元素材名と完全SHA-256が両方一致する候補を採用する。プールの配布名は、目録の元素材名へ照合したうえで使う。検索範囲は承認されたフォルダーのみ。候補が複数の場合は選択を求める。期待ハッシュ欠落、照合失敗、素材や目録の差替えでは該当行を置き換えない。 [候補の照合と選択](../../YMMProjectManager/Relinking/UI/RelinkStrictSearch.cs#L72) · [候補ハンドルの保持](../../YMMProjectManager/Assets/Pool/AssetPool.cs#L220)
3. 選択した1〜10件を利用者が確認する。保存先は元PFと同じフォルダーの空き `.ymmp` 名。選択した版が現在の先端か、元PF・対象参照・素材・履歴・指定した目録が承認時の証拠と一致するかを再確認する。該当するJSON参照のみを変更した作業ファイルを作成し、既存先を上書きせず公開する。 [保存前の照合](../../YMMProjectManager/Relinking/UI/RelinkStrictSearch.cs#L127) · [管理付き保存](../../YMMProjectManager/Presentation/Relink/RelinkMainWindow.xaml.cs#L347) · [JSONの対象参照更新](../../YMMProjectManager/Relinking/RelinkJsonUpdate.cs#L11)
4. **内部素材のみ**の場合、元系列に版を追記する。利用者が関連付け移行を選ぶと、同じProject IDの一覧登録を別名の出力先へ切り替える。**外部素材を含む場合**は、元PF・元の登録・元履歴を保ち、独立した派生IDと派生系列を作って新PFを登録する。外部素材は承認済みの場所のまま参照し、コピーや取得は行わない。 [内部素材の関連付け確認](../../YMMProjectManager/Presentation/Relink/RelinkMainWindow.xaml.cs#L455) · [外部素材での派生](../../YMMProjectManager/Relinking/UI/RelinkStrictSearch.cs#L175) · [派生と公開のADR](../architecture/adr/external-relink-derivation-r22.md)
5. 出力作成または登録が中断された場合は「再リンクの回復」で該当記録を選ぶ。操作ID、元・出力PFの物理IDとSHA-256、関連版、素材候補、外部保管の再承認を照合する。既に確定した同一出力・派生ID・版は照合して再利用する。公開記録欠落・破損や同一内容の別ファイルとの入替えなどでは停止し、元PF・既存出力・履歴・回復記録を保持する。 [管理再リンクの公開と回復](../architecture/adr/managed-relink-publication-r16.md) · [外部素材回復の判定](../../YMMProjectManager/Relinking/AssetExternalRelinkRecovery.cs#L97)

回復記録は元PFと同じディレクトリに保持する。管理再リンクの `.ymmpm-relink-<操作ID>.json` はv1〜v5を読み込む。v4以降は元・作業ファイルの物理IDと完全SHA-256を操作に結び付け、現行の厳格管理再リンクが作るv5はさらに、行ごとの元素材名と検証した目録の証拠を持つ。旧形式にない公開証拠の照合はできない。外部素材の回復記録は別のv1で `.ymmpm-relink-external-<操作ID>.json`、派生版の格納先は `.ymmpm-derived-<派生ID>`。公開済み出力を照合する `.publish` ファイルも回復記録と対で保持する。これらはローカル用の記録であり、Git公開の対象から除外される。 [厳格な回復記録](../../YMMProjectManager/Relinking/AssetRelinkRecovery.cs#L89) · [公開証拠](../../YMMProjectManager/Relinking/AssetRelinkPublication.cs#L80) · [派生・回復の保存位置](../../YMMProjectManager/Relinking/AssetExternalRelinkRecovery.cs#L12)

上段の「再リンク」は編集中Timeline向けの別経路。候補探索と、対象・行を再照合した候補ハッシュ表示まで。期待ハッシュの一致は判定せず、Timelineへの直接反映は停止中。PF整理はYMMPX同梱時のUI情報選択に関係するため、YMMPXの章を参照。 [Timeline側操作](../../YMMProjectManager/Presentation/Relink/RelinkMainWindow.xaml.cs#L273)

## 仕様を支える回帰

| 対象 | 主に照合するテスト |
| --- | --- |
| 一覧保存・下書き・外部競合 | [通常保存](../../YMMProjectManager.Tests/ProjectListSaveTests.cs)、[同時実行](../../YMMProjectManager.Tests/ProjectListConcurrencyTests.cs)、[公開中断・外部置換](../../YMMProjectManager.Tests/ProjectListPublicationTests.cs)、[下書き](../../YMMProjectManager.Tests/ProjectListDraftTests.cs)、[一覧の回復](../../YMMProjectManager.Tests/ProjectListRecoveryTests.cs) |
| ID・旧世代 | [重複修復と原本保持](../../YMMProjectManager.Tests/ProjectListIdentityRecoveryTests.cs)、[旧世代の別名救済](../../YMMProjectManager.Tests/LegacyGenerationExportTests.cs) |
| 素材・履歴・プール | [素材インベントリー](../../YMMProjectManager.Tests/AssetInventoryTests.cs)、[素材メタデータと履歴](../../YMMProjectManager.Tests/AssetManifestTests.cs)、[素材プールの目録・競合](../../YMMProjectManager.Tests/AssetPoolTests.cs) |
| 再リンク | [厳格再リンク](../../YMMProjectManager.Tests/StrictRelinkWorkflowTests.cs)、[参照更新とTimeline保全](../../YMMProjectManager.Tests/RelinkIntegrityTests.cs)、[内部素材の関連付け](../../YMMProjectManager.Tests/RelinkAssociationTests.cs)、[外部素材の回復](../../YMMProjectManager.Tests/ExternalRelinkRecoveryTests.cs)、[派生登録の回復](../../YMMProjectManager.Tests/DerivedRelinkAssociationTests.cs) |

R34までの製品回帰は隔離ビルドと合成ファイル・テスト用の画面で実施済み。現行候補での実YMM4・実NASの動作、旧ファイル全種の互換性、電源断時の保全は未検証。 [検証実績と残る実機受入](../development/v0.5.0-r34-results.md) · [受入シナリオ](../development/v0.5.0-r34-scenarios.md)
