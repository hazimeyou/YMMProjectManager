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
- Internal ID 精度統計（match by id / fallback / unmatched）
- DiffTimeline prototype（experimental）
- DiffTimeline visible-range filtering + navigation
- Timeline sync UX (CurrentFrame / jump / selection sync)
- PureTimeline adapter boundary and fallback
- FutureYmmTimelineAdapter scaffold
- Experimental YMM TimelineView host PoC（default disabled）

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

Diff/Snapshot の性能検証と correctness 検証のため `YMMProjectManager.Benchmarks` を追加しています。

実行:

`dotnet run --project YMMProjectManager.Benchmarks/YMMProjectManager.Benchmarks.csproj`

出力先:

- `logs/benchmarks/benchmark-yyyyMMdd-HHmmss.md`
- `logs/benchmarks/correctness-yyyyMMdd-HHmmss.json`

---

# ライセンス

本ソフトウェアは MIT License のもとで公開されています。

## preview12 Notes

- Experimental YMM TimelineView host PoC
- Disabled by default (`EnableExperimentalYmmTimelineHost = false`)
- Isolated host + fallback design
- Dispose safety diagnostics
