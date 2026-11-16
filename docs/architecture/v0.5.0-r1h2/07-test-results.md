# H2 テスト結果

Run ID `r1h2-20261117-01`。基準 R1-H `0e3d287ee034048b045c79e199509aed7e49a4ef`。参照 DLL は R1-H test output から新規 temp にコピーし、前回記録と SHA-256 の一致を確認。build の YMM4_PATH はその temp を指定し、AfterTargets のコピー先も temp 内。runner は別の新規 TEMP/TMP/YMM4DirPath、空の YMMPX_LIB_PATH と YMM_TEST_FILTER で実施した。

```powershell
dotnet build .\YMMProjectManager.sln -c Release "-p:YMM4_PATH=<隔離参照DLLディレクトリ>" --nologo
dotnet run --project .\YMMProjectManager.Tests\YMMProjectManager.Tests.csproj -c Release --no-build
```

両方 exit 0、Release build 0 warnings / 0 errors。既存・R1-A〜R1-C regression runner が正常終了。runner は既存の個別結果を出力しない。実 YmmpxLib 条件付き試験は未実施。これらは guest / YMM4 実ホスト受入ではない。

| ID | 判定 | 内容 |
|---|---|---|
| H2-T01 | BLOCKED | OS・hypervisor の一部は読取、管理機能の状態は権限不足 |
| H2-T02 | BLOCKED | 管理ツール未検出、既存 VM / 使用可能基盤は不明 |
| H2-T03 | PASS | CPU・メモリ・fixed disk の現在値を読取。追加保存先と guest GPU は不明 |
| H2-T04 | PASS | 構成候補と前提を設計。最終構成は未確定 |
| H2-T05–T19 | BLOCKED | VM / guest 不在。共有・権限・復元・配置・起動は未検証 |
| H2-T20 | BLOCKED | 本番への意図的操作なし。全本番ファイルの前後比較はしていない |
| H2-T21 | PASS | Release build、警告0・エラー0 |
| H2-T22 | PASS | 既存・R1-A〜R1-C runner exit 0、実ホストなし |

参照 DLL SHA-256: YukkuriMovieMaker.dll `A5CAB5377EB4A45C8A93B8BD0ED0F578E541FCBDF51E2D21DB959D2D1B8FDB66`、Plugin `2252B888D07EE8925BCD8BA4EDE83F782C025AECB17CB256B2BE172AD77DF6EF`、Controls `C38022666BB440D448D1E22FAAE4D6B716912DABB55DA9454409525845EBA8B9`。これらは既存隔離 build output と一致する参照 DLL の証拠であり、guest や plugin load の証明ではない。
