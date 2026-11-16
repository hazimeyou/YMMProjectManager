# Track B テスト

Run ID `r1h-20261117-01`、基準 `d2d46cb8f86ad51b508585760d96f8985e6c3628`、Windows 11 Pro、SDK 10.0.404。R1-C worktree の test build output にあった参照 DLL 3個を新規 temp にコピーし、YMM4_PATH をそのディレクトリへ指定。build target が行う plugin のコピーもその temp 配下。TEMP/TMP/YMM4DirPath を別の新規 temp、YMMPX_LIB_PATH / YMM_TEST_FILTER を空にして runner 実行。独立 fixture のみであり YMM4 ホストの分離受入ではない。

```powershell
dotnet build .\YMMProjectManager.sln -c Release "-p:YMM4_PATH=<新規隔離DLLディレクトリ>" --nologo
dotnet run --project .\YMMProjectManager.Tests\YMMProjectManager.Tests.csproj -c Release --no-build
```

両コマンド exit 0、Release build warnings 0 / errors 0。既存、R1-A / A2 / B / B2 / C runner はまとめて正常終了し、個別結果は runner が出力しない。実 YmmpxLib 条件付き試験は未実施。build と test はホスト E2E を意味しない。

| ID | 結果 | 根拠・範囲 |
|---|---|---|
| B-T01 | PASS | 方式を比較、VM は候補 |
| B-T02 | BLOCKED | product code の静的列挙。YMM4 / OS / external の実行時網羅性未確認 |
| B-T03–B-T07 | BLOCKED | 専用環境・書込先の実効分離未確認 |
| B-T08 | BLOCKED | 現 token の実効 group / privilege は確認。試験主体の本番拒否は未確認 |
| B-T09 | BLOCKED | 実 VM と復旧実演なし、計画のみ |
| B-T10–B-T12 | BLOCKED | 隔離未成立のため起動・plugin・終了を実行せず |
| B-T13 | BLOCKED | 本番データを対象とする意図的操作・配置なし。全本番データの before/after 比較なし |
| B-T14 | PASS | Release build。隔離 DLL に出力コピー |

参照 DLL SHA-256: `YukkuriMovieMaker.dll` `A5CAB5377EB4A45C8A93B8BD0ED0F578E541FCBDF51E2D21DB959D2D1B8FDB66`; `YukkuriMovieMaker.Plugin.dll` `2252B888D07EE8925BCD8BA4EDE83F782C025AECB17CB256B2BE172AD77DF6EF`; `YukkuriMovieMaker.Controls.dll` `C38022666BB440D448D1E22FAAE4D6B716912DABB55DA9454409525845EBA8B9`。これらは参照 DLL の同一性でありホスト実行証拠ではない。
