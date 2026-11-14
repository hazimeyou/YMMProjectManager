# 検証結果

Windows x64、.NET SDK 10.0.404。YMM4 v4.57.0.1 の隔離参照 DLL、TEMP / TMP / YMM4DirPath は新規専用ディレクトリ、実ユーザー世代データなし。

```powershell
$libs = Join-Path $env:TEMP 'ymm-r0-audit-libs'
dotnet build .\YMMProjectManager.sln -c Release "-p:YMM4_PATH=$libs" --nologo
# 新規 TEMP / TMP / YMM4DirPath、空の YMMPX_LIB_PATH / YMM_TEST_FILTER を設定
dotnet run --project .\YMMProjectManager.Tests\YMMProjectManager.Tests.csproj -c Release --no-build
```

両方 exit 0、build warning 0 / error 0。既存 runner は個別結果を出力しない。R1-A、R1-A2、R1-B と既存 Generation test を同一 runner で実施。実 YmmpxLib 条件付き試験は DLL 不在で未実施。

| ID | 判定 | 検証範囲 |
|---|---|---|
| T01 | PASS | 初回作成と既存空状態テスト |
| T02 | PASS | schema 1 の空 Generations |
| T03 | PASS | 2世代一覧 |
| T04–T08 | PASS | 不正 JSON、空、欠落、型不正、未対応 schema。書込みを拒否 |
| T09–T11 | PASS | 作成拒否・manifest / snapshot / metadata hash 保持 |
| T12 | PASS | 修復した fixture に追加し既存2世代を保持 |
| T13 | BLOCKED | 事前キャンセルと置換共有違反は保護確認。書込み途中の I/O 故障は注入境界がなく未実施 |
| T14 | PASS | 読込開始時の共有違反による IOException。読込途中の故障は未実施 |
| T15 | PASS | 読込および置換の共有違反で保護 |
| T16 | PASS | 既存正常 manifest の元ファイル復元 |
| T17 | PASS | manifest 欠落・snapshot 残存時に作成・削除拒否 |
| T18 | PASS | Service 例外と削除結果で失敗伝播。実 UI 未検証 |
| T19 | PASS | 合成 fixture を元に戻した後に再作成 |
| T20 | PASS | 既存 Generation test runner 正常終了 |

正常時の表示とエラー表示の実 YMM4 ホスト受入は実施していない。
