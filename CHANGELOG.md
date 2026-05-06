# Changelog

## v0.2.9-history-preview5

- Added Internal ID match statistics
  - `YmmDiffMatchStatistics`
  - `YmmProjectDiffResult`
  - `YmmProjectDiffService.DiffWithStatistics(...)`
- Added diff correctness benchmark fixtures
  - `same-text-multiple`
  - `same-filepath-multiple`
  - `moved-frame`
  - `moved-layer`
  - `modified-text`
- Added correctness benchmark output
  - `logs/benchmarks/correctness-yyyyMMdd-HHmmss.json`
- Added experimental `DiffTimelineView` prototype
  - read-only timeline rectangles (Frame x Layer)
  - click-to-detail
  - integrated into `ProjectDiffWindow` tab
- Continued YMM4-Timeline investigation (design reference only, no direct dependency)

## v0.2.9-history-preview4

- Internal Item ID PoC を追加
  - `InternalItemIdService / InternalItemIdOptions / InternalItemIdentity`
  - Snapshot metadata に `internalItemIdVersion: 1` を追加
  - YMM意味差分で InternalItemId 一致を優先し、既存 matching へ fallback
  - `Moved` 判定（Frame/Layer/TimelineIndex 変化）を追加
- ベンチマーク基盤を追加
  - `YMMProjectManager.Benchmarks` プロジェクトを追加
  - `small / medium / large / extreme` シナリオを実装
  - 結果を `logs/benchmarks/` に出力
- v0.3.x の Issue 粒度分解ドキュメントを追加
  - `docs/v0.3x-issue-breakdown.md`

## v0.2.9-history-preview3

- 履歴・差分機能を `preview / experimental` として位置づけを明確化
- README に preview 注記を追加
- 今後の設計方針（基盤優先、DiffTL方針、純正TL+DiffTL構成）を整理

## v0.2.9-history-preview2

- `.ymmp` スナップショットの作成機能を追加
- スナップショット一覧表示と削除機能を追加
- スナップショット同士の比較を追加
- 現在ファイルとスナップショットの比較を追加
- JSON正規化と JSON 差分表示を追加
- `Text / FilePath / Frame / Layer / Length` を対象にした YMM 意味差分の基盤を追加
- プロジェクト一覧の右クリックメニューに機能を追加

## v0.2.9-history-preview1

- 履歴・差分機能の初期導入

### Not Included (Preview Series)

- YMMPX
- Git 連携
- ブランチ / マージ / コンフリクト解決
- 差分からの復元
- 複数人編集
