# テスト結果

Windows x64、.NET SDK 10.0.404、隔離 DLL YMM4 v4.57.0.1。すべて合成 fixture、テスト runner の TEMP / TMP / YMM4DirPath は新規一時ディレクトリ。実 YMM4 を起動していない。

```powershell
$libs = Join-Path $env:TEMP 'ymm-r0-audit-libs'
dotnet build .\YMMProjectManager.sln -c Release "-p:YMM4_PATH=$libs" --nologo
# 別の新規ディレクトリを TEMP, TMP, YMM4DirPath に設定し、YMMPX_LIB_PATH と YMM_TEST_FILTER を空にする
dotnet run --project .\YMMProjectManager.Tests\YMMProjectManager.Tests.csproj -c Release --no-build
```

Build 終了コード 0、warning 0、error 0。runner 終了コード 0、既存テストは個別結果を出力しない。新規テストは最後に成功メッセージを出す。実 YmmpxLib 条件付き試験は DLL 不在で未実施。

| ID | 結果 | 内容 |
|---|---|---|
| T01 | PASS | 新規保存、JSON parse、再読込、temp なし |
| T02 | PASS | 既存を置換、再読込 |
| T03 | PASS | 事前キャンセル、bytes / length / SHA-256 / parse / 再読込維持 |
| T04 | PASS | 注入 writer で一時ファイルを半分書きキャンセル、元維持 |
| T05 | PASS | 部分書込み後 IOException、元維持 |
| T06 | PASS | 書込み後、置換前の IOException とキャンセル、元維持 |
| T07 | PARTIAL | Windows の共有違反による置換拒否を確認。ACL アクセス拒否は未実施 |
| T08 | PASS | 合成 disk-full 相当 IOException、実容量不足は未実施 |
| T09 | PASS | 別 repository instance で再読込 |
| T10 | PASS | 失敗後に別 instance で元一覧を再読込 |
| T11 | PASS | 連続置換と最終内容 |
| T12 | PASS | 同一プロセス、別 instance の12並行保存、完全な1件の JSON。順序は未保証 |
| T13 | PASS | 合成旧形式の名前・Pinned・LastAccess・linked file を round trip。未知フィールドは対象外 |
| T14 | PARTIAL | repository の IOException / cancellation の伝播を検証。ViewModel と UI はコード追跡のみ |

T03〜T08 の検証後は元ファイルの hash・サイズ・バイト・JSON・再読込と temp がないことを確認。破損 JSON と共有違反で Load が例外を返すことも確認。各テストは例外で runner を停止する。
