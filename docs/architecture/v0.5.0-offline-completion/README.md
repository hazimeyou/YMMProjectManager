# v0.5.0 オフライン作業 — PARTIAL

基準 6206e76acb4470b12cb7d178d463e063aa44b310。別系統 7fa8bdb の checkpoint / relink 作業と未コミット UI 差分は未統合で保護した。基準の Assembly Version は 0.4.0。v0.5.0 リリースを主張しない。

| 機能 | 判定 | 根拠・残作業 |
|---|---|---|
| Git | PARTIAL | 新規内部 API にローカル init/detect/status/stage/unstage/commit/history/branch。UI未接続。clone/fetch/pull/push、競合や credential 検証未実装 |
| GitHub | MISSING | remote 検出・認証・network failure 未実装。実リモートへの push なし |
| 素材 metadata | PARTIAL | 明示指定の相対ファイルの SHA-256/size、missing・mismatch、HTTPS URL、create-only JSON export/load。既存 project scan / UI・relink へ未接続。自動 upload なし |
| project list | PARTIAL | R1-A/A2 の保存・失敗復旧の回帰あり。実 YMM4 受入未実施 |
| relink | PARTIAL | R1-C の安全保存回帰あり。特殊 PSD/連番と実ホストは既存制限あり |
| generation / checkpoint | DEPRECATED | 既存データ保護を維持。今回廃止・移行なし |
| YMMPX | UNVERIFIED | 外部 YmmpxLib に最終出力先を直接渡す経路あり。失敗・既存出力保護は実ライブラリで未検証 |
| timeline / thumbnail | PARTIAL | 既存 offline 回帰あり。実 YMM4 の座標・表示は未検証 |
| UI / release | PARTIAL | 新規機能未接続、version 0.4.0、release package と独立レビュー未完了 |

## 実装と制約

Git は shell を使わず `ArgumentList`、明示 working directory、終了コード、stdout/stderr、cancel、2分 timeout を使用。通常の Git 設定と hook は Git が実行しうる。戻り値を秘密情報除去済みとみなしてログ保存しない。リモート操作や UI は未提供。Git 設定・環境変数、巨大出力、キャンセル後の永続状態のセキュリティ評価は未完了。Stage は repo 内の明示相対パスのみで literal pathspec を使用。

素材 export は同一ディレクトリの一意 temp へ書き flush 後、既存宛先に上書きせず移動。元ファイルは変更しない。URL は HTTPS かつ userinfo/query/fragment を拒否。各素材を手動で指定する API であり、汎用 project 互換検出ではない。reparse point は検出時に拒否するが検査後の差し替え等の TOCTOU 防御は未完了。create-only のため manifest 更新・移行は未実装。URL が秘匿情報をパスに含む可能性を排除できないため無条件で公開しない。

## 検証

.NET SDK 10.0.404、Git 2.55.0.windows.5。R1-C のテスト出力にあった3つの参照 DLL を一意な temp に複製して `YMM4_PATH` に指定し、ビルド後コピーも隔離先に限定。TEMP/TMP も隔離し `YMMPX_LIB_PATH` と `YMM_TEST_FILTER` は空。

```powershell
dotnet build .\YMMProjectManager.sln -c Release "-p:YMM4_PATH=<isolated refs>" --nologo
dotnet run --project .\YMMProjectManager.Tests\YMMProjectManager.Tests.csproj -c Release --no-build
```

いずれも exit 0。build warnings 0 errors 0。runner は既存回帰と Git local / asset metadata checks を出力し正常終了。総テスト数・skip 数は runner が報告しない。実 YmmpxLib 条件付きケースは未実施。上記は実ホスト互換性の証明ではない。

## レビュー / 次の作業

独立レビューは未実施。安全性上の未解決事項は YMMPX 実装の出力・展開境界、Git リモート・credential・hook、素材 reparse TOCTOU、保存と UI の統合。これらを含むリリースゲートは未達。次は既存の未コミット UI と別系統のコードを確認し、適用範囲を保護した上で Git の local bare remote 回帰・安全な実装と UI 接続を継続する。

## 共有 VM 受入準備 — WAITING_FOR_SHARED_VM

共有 VM の起動・停止・チェックポイント操作・インストールはしていない。受入前に確定 commit とビルド成果物の SHA-256、依存 DLL・YMM4版・runtime の互換性を固定し、YMMTestLabManager の承認済み経路を使う。操作対象は合成 project と素材のみ。確認項目は plugin 読込、一覧保存失敗通知と復旧、再リンクの外部変更拒否と特殊素材、YMMPX 作成・展開、timeline seek、UI のキャンセル・失敗表示。データ不一致・予期しない書込み・例外は失敗、ログと一時ファイルの所有関係を記録する。正確な所要時間と削除手順は環境確定後に記載する。現状は完成した受入パッケージではない。
