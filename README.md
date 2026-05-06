# YMM Project Manager

YukkuriMovieMaker 用の補助ツールです。

本ツールは以下の機能を提供します。

- 素材再リンク（自動探索）
- サムネイル生成
- `.ymmp` 履歴・差分（preview）

---

# インストール方法

1. GitHub の Release ページから `.ymme` ファイルをダウンロードします
2. ダウンロードした `.ymme` を起動します
3. YukkuriMovieMaker の「ツール」タブから本ツールを実行します

---

# 主な機能

## 素材再リンク

.ymmp ファイル内の素材（画像 / 動画 / 音声）のリンク切れを検出し、自動探索で修復します。

## プロジェクト一覧表示

.ymmp ファイルを登録してプロジェクトの一覧を表示します。登録したプロジェクトはサムネイル付きで管理できます。

## 履歴・差分（Preview / Experimental）

履歴・差分機能は experimental / preview 機能です。

今後、内部形式・UI・差分モデル・スナップショット仕様が変更される可能性があります。

現在利用できる機能:

- スナップショット作成
- スナップショット一覧表示
- スナップショット削除
- スナップショット同士の比較
- 現在ファイルとスナップショットの比較
- JSON差分表示
- `Text / FilePath / Frame / Layer / Length` の意味差分（基盤）
- Internal Item ID PoC（互換保証なし）

履歴保存先:

`%AppData%\YMMProjectManager\history\projects\<projectKey>\snapshots\<snapshotId>`

`projectKey` は `SHA256(full normalized project path)`、`snapshotId` は `yyyyMMdd-HHmmssfff` です。

対象外（将来対応）:

- YMMPX
- Git連携
- ブランチ / マージ / コンフリクト解決
- 差分からの復元
- 複数人編集

---

## ベンチマーク（Preview）

Diff/Snapshot の性能検証用に `YMMProjectManager.Benchmarks` を追加しています。

実行:

`dotnet run --project YMMProjectManager.Benchmarks/YMMProjectManager.Benchmarks.csproj`

出力先:

`logs/benchmarks/`

シナリオ:

- `small`
- `medium`
- `large`
- `extreme`

---

## 再リンクの使い方

1. プロジェクトを開きます
2. `開いているPFを再リンク`をクリックして、素材再リンクウインドウを開きます
3. `ファイル検索`をクリックして、素材の再リンク処理を開始します
4. 結果を確認して、必要に応じて候補を選択して修正します
5. `更新内容を保存`をクリックして、プロジェクトを更新します
6. 自動でタイムライン上の素材リンクが更新され、再リンクされます

---

## サムネイル生成

プロジェクトのプレビューからサムネイルを生成します。

- 64点サンプリング
- 非同期処理
- UI操作に影響を与えない設計

注意:

- YMM の表示状態に依存します
- プレビューが更新されない場合は取得できないことがあります

---

# ライセンス

本ソフトウェアは MIT License のもとで公開されています。
