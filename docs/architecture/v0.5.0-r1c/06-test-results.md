# テスト結果

Windows x64、.NET SDK 10.0.404、隔離 YMM4 参照 DLL v4.57.0.1。別の新規 temp を TEMP / TMP / YMM4DirPath にし、合成データのみを操作した。実ホストは未実施。

```powershell
dotnet build .\YMMProjectManager.sln -c Release "-p:YMM4_PATH=<隔離DLLディレクトリ>" --nologo
dotnet run --project .\YMMProjectManager.Tests\YMMProjectManager.Tests.csproj -c Release --no-build
```

最終 build と runner は exit 0、警告0・エラー0。途中で隔離 DLL ディレクトリが見つからなくなり参照不足で build が失敗したが、前工程の隔離出力から DLL をコピーし再実行した。runner は既存ケースの個別結果を出力しない。実 YmmpxLib 条件付きテストは未実施。

| ID | 判定 | 確認範囲 |
|---|---|---|
| T01–T06 | PASS | 複数値・同値別候補・Items外・未知フィールド・空白とエスケープを全体比較 |
| T07–T08 | PASS | scan後と置換前の外部変更拒否、同じ初期状態の同時保存1件成功 |
| T09–T12 | PASS | 事前キャンセル、部分書込み、flush 注入、共有違反で元 hash 維持 |
| T13 | PASS | 既存 backup の hash を維持し別名 backup を作成 |
| T14–T15 | BLOCKED | PSD と VideoItem PNG は正常更新の互換性未確認、安全側で拒否 |
| T16–T17 | PASS | 合成 Unicode / 日本語、JSON escape、UTF-8 BOM 保持 |
| T18–T20 | PASS | 重複行・ネストした plugin FilePath を拒否、失敗伝播、再読込 |
| T21 | NOT_RUN | 基準に独立した既存再リンク自動テストなし。実ホスト回帰未確認 |
| T22–T27 | PASS | 既存 R1-A / A2 / B / B2 と R1-C、Release build、runner |

T09–T12 の合成元プロジェクト SHA-256 は前後とも `1CAF17E74A6720444E0F662A08D475DFC16EC7056225B5ED88DF57DEDE09F05F`。期待結果は各ケースで独立実行し、成功時は JSON 全体を期待バイト列と比較した。
