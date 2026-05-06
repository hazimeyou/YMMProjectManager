# Pure Timeline Fallback Design (preview10)

## Goal

Pure Timeline 統合が失敗しても、DiffTimeline（DiffTL）単体運用を維持する。

## Must Not Break

- `ProjectDiffWindow` が閉じない
- DiffTL の表示・選択・ナビゲーションが継続する
- CurrentFrame line が表示継続する
- List View / Timeline View の切替が継続する

## Adapter Failure Behavior

- `IPureTimelineAdapter.InitializeAsync` 失敗時:
  - `PureTimelineHost.Status = Error`
  - `PureTimelineHost.LastAction` に失敗理由を保存
  - `TimelineSyncState = Detached` へ切り替え
  - DiffTL は standalone 継続

- `SetCurrentFrameAsync` / `CenterFrameAsync` 失敗時:
  - Pure Timeline 側のみ Error 状態化
  - DiffTL ナビゲーションは継続

- `DisposeAsync` 失敗時:
  - 例外でアプリを落とさない
  - Error 状態を表示して終了処理を継続

## Retrieval Failure (Future Adapter)

- TimelineView 取得失敗時は Error として扱う
- 再試行可能な設計を維持する（再初期化導線）
- 失敗中も DiffTL の機能は制限しない

## Fallback Modes

- `Synced`: Pure TL 側が利用可能な前提
- `Detached`: Pure TL 不可 or 手動切断
- `Standalone`: DiffTL 単独の安全運転モード

## Preview10 Output

- `IPureTimelineAdapter` 境界を導入
- `PlaceholderPureTimelineAdapter` を基準実装として使用
- 本統合未実装でも UI と操作導線を維持
