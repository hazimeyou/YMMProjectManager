# 書き込み面と分離状況

R1-C のコードを静的調査。**本表は YMM4 本体・プラグイン・外部ライブラリの網羅的な実行時 tracing ではない。** 相対パスは repository root。

| 面 | 確認した利用箇所 | 状態 |
|---|---|---|
| 一覧 | `Infrastructure/JsonProjectRepository.cs:133`: `YMM4DirPath/user/plugin/YMMProjectManager/data/projects.json`、未設定時 Roaming AppData | ゲスト内で両条件を確認必要 |
| Generation | `Infrastructure/Generations/ProjectGenerationStorage.cs:34`: Roaming AppData `YMMProjectManager/Generations` | 環境変数だけで OS SpecialFolder の実態を保証しない |
| settings | `Settings/YMMProjectManagerSettings.cs:141`: plugin assembly のディレクトリ下 `settings` | 個別 plugin と YMM 本体の設定が別と確定したわけではない |
| plugin / build | `YMMProjectManager.csproj:29`: 指定 YMM4Path が存在すると build 後に DLL を `user/plugin/YMMProjectManager` にコピー | ビルド用ディレクトリは新規 temp に隔離し、その中だけにコピー |
| logger | `Plugin/ToolPluginEntry.cs:33`、`Presentation/ViewModels/ProjectListViewModel.cs:840`、`Presentation/Controls/ScrubThumbnailControl.xaml.cs:141`: assembly 近傍 logs | ゲスト組立先を確認する必要あり |
| cache | `Infrastructure/Output/SeekPreviewThumbnailGenerator.cs:61` 他は YMM `AppDirectories.UserDirectory` 下 | YMM API の実際の解決先と各経路は未確定 |
| TEMP | current preview / seek probe / benchmark / diagnostics / installer は `Path.GetTempPath()` を使用 | guest TEMP/TMP の実効先を観測する必要あり |
| project と資材 | `Infrastructure/Relink/YmmpMediaRelinker.cs:560,573`、世代復元、同梱・展開はユーザー選択のパスを操作 | 合成 fixture と出力先だけを扱い、VM に本番データを接続しない |
| 外部ライブラリ | `Infrastructure/Packaging/YmmpxLibResolver.cs:12`、`YmmpxLibInstallGuide.cs:164`: 環境変数 / 祖先ディレクトリを探索、temp に installer ダウンロード、起動可能 | 実 YmmpxLib の入出力、network、依存先は未確認 |
| LocalAppData / Documents / registry | この検索の product code に明示的な書込みは見当たらない | YMM4 本体、WPF、依存 DLL、OS の使用は未確認。使用しないとは結論しない |

R1-A2 の保存関連独立テストは temp root と runtime 設定を差し替えたが、YMM4 実ホストの profile 全域へのアクセス拒否を証明していない。
