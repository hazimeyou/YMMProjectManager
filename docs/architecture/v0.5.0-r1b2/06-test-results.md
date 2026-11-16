# テスト結果

Windows x64、.NET SDK 10.0.404。R1-B の test output に残る YMM4 v4.57.0.1 参照用 DLL 3個を新規 temp にコピーし、build の YMM4_PATH をその隔離ディレクトリに設定。TEMP / TMP / YMM4DirPath は別の新規 temp。実ホスト起動なし。

```powershell
dotnet build .\YMMProjectManager.sln -c Release "-p:YMM4_PATH=<隔離DLLディレクトリ>" --nologo
dotnet run --project .\YMMProjectManager.Tests\YMMProjectManager.Tests.csproj -c Release --no-build
```

最終 build exit 0、warnings 0、errors 0。runner exit 0。既存 runner は各ケースを個別出力しない。YmmpxLib の条件付きテストは未実施。実機 E2E は未実施。最初の修正後テストで削除用の外部変更 fixture が1回目と同内容だったため失敗し、fixture を毎回異なる内容に変更して再検証した。

| ID | 結果・範囲 |
|---|---|
| T01 | PASS: 正常世代作成・既存維持 |
| T02–T03 | PASS: 部分 temp 書込み後の合成 IOException（容量不足相当） |
| T04 | PASS: flush hook による合成 IOException |
| T05 | PASS: OS の共有違反による置換拒否 |
| T06 | PASS: 通常の失敗時に自分の temp を除去、無関係な固定 tmp を保持。cleanup 自体の失敗は未注入 |
| T07 | BLOCKED: R1-B の既存固定 temp 上書き・削除は実測。二つの writer の同時競合は未測定 |
| T08 | PASS: 同一 Manifest への2回の書込みで異なる temp 名と先行固定 tmp の保持 |
| T09 | PASS: 同一 revision 2 writer は1成功・1競合、同一 project の8作成で成功分を保持 |
| T10 | PASS: 別 Manifest 2件が同期 barrier で同時に書込みへ到達 |
| T11–T12 | PASS: 作成・削除の比較前に外部内容変更し、拒否と変更保持 |
| T13 | PASS: 比較後〜置換間の競合窓をコード評価。完全排他は未実装 |
| T14–T16 | PASS: 作成・削除の障害、rollback と旧 snapshot hash |
| T17 | PASS: 旧 ID の別名復元 |
| T18–T20 | PASS: R1-B 回帰で破損・未対応・欠落と旧 snapshot 残存を検証 |
| T21–T24 | PASS: 既存 R1-B、R1-A / A2 回帰、Release build、既存 runner |

hash 証拠と手順の詳細は障害注入資料。合成テストと実ホスト受入を混同しない。
