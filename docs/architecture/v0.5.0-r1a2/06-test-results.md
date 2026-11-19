# テスト結果

Windows x64 / .NET SDK 10.0.404、隔離 YMM4 v4.57.0.1 DLL。基準 R1-A の再現確認後、修正後に次を実行。

```powershell
$libs = Join-Path $env:TEMP 'ymm-r0-audit-libs'
dotnet build .\YMMProjectManager.sln -c Release "-p:YMM4_PATH=$libs" --nologo
# 新規隔離ディレクトリを TEMP / TMP / YMM4DirPath に設定し、YMMPX_LIB_PATH / YMM_TEST_FILTER を空にする
dotnet run --project .\YMMProjectManager.Tests\YMMProjectManager.Tests.csproj -c Release --no-build
```

Build exit 0、warnings 0、errors 0。runner exit 0、既存 runner は個別結果を出さない。R1-A regression と R1-A2 independent WPF regression を実行。実 YmmpxLib 条件付き試験は DLL 不在で未実施。各状態は以下の証拠範囲に限る。

| ID | 判定 | 内容 |
|---|---|---|
| T01 | PASS | 独立 repository の保存・別 instance 読込 |
| T02 | PASS | 独立 WPF ViewModel の追加・削除 IOException 時にダイアログを観測。文面未検証 |
| T03 | PASS | 独立 WPF の synthetic cancellation で I/O 警告なし、保留 |
| T04 | PASS | 追加・削除・登録の変更を ViewModel 上に保持 |
| T05 | PASS | 保存専用 retry |
| T06 | PASS | 別 repository で再読込 |
| T07 | PASS | 不正 JSON で VM 初期化失敗・警告・保存防止 |
| T08 | PASS | 0 bytes で同様に失敗・保護 |
| T09 | PASS | 共有ロック下の読込エラー、警告、hash 不変 |
| T10 | BLOCKED | ACL 書込拒否未実施 |
| T11 | BLOCKED | ACL 置換拒否未実施 |
| T12 | BLOCKED | ACL 回復後の実検証未実施。合成エラー解除後の retry は T05 |
| T13 | PASS | 追加・削除の独立 ViewModel 保存失敗と警告。開く操作は未実施 |
| T14 | PASS | 登録 method の IOException 再送出と retry を独立試験。実展開 UI は未実施 |
| T15 | BLOCKED | YMM4 実ホスト成功未実施 |
| T16 | BLOCKED | YMM4 実ホスト失敗通知未実施 |
| T17 | BLOCKED | YMM4 実ホスト retry 未実施 |
| T18 | PASS | 合成元の SHA-256 同一、詳細は回復資料 |

PASS は記載した独立試験の範囲を示す。実機項目の代替ではない。
