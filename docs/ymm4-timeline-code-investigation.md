# YMM4-Timeline Code Investigation (preview11)

Target repository: [routersys/YMM4-Timeline](https://github.com/routersys/YMM4-Timeline)  
Checked on: 2026-05-06 (JST)  
Branch/HEAD seen during investigation: `main` / `755a664`

## Confirmed Structure

- Plugin entry point:
  - `Timeline/TimelineToolPlugin.cs`
  - `IToolPlugin` を実装し、`ViewModelType` と `ViewType` を公開
- Timeline UI host:
  - `Timeline/TimelineToolView.xaml`
  - `YukkuriMovieMaker.Views.TimelineView` を直接配置
- Timeline VM bridge:
  - `Timeline/TimelineToolViewModel.cs`
  - `IToolViewModel`, `ITimelineToolViewModel`, `IDisposable` を実装

## Key Integration Points (Confirmed)

- `SetTimelineToolInfo(TimelineToolInfo info)` が Pure TL 接続の入口
- Scene 解決:
  - `info.Scenes.AllScenes.FirstOrDefault(s => s.Timeline == info.Timeline)`
- TimelineViewModel 生成:
  - `new TimelineViewModel(scene, info.UndoRedoManager, info.AsyncAwaitStatus)`
- Scene/Timeline の切替対策:
  - 再セット前に `DisposeTimelineViewModel()` を呼び、古い VM を破棄
- Dispose:
  - `TimelineToolViewModel.Dispose()` で
    - `currentTimeline.PropertyChanged -= Timeline_PropertyChanged`
    - `TimelineViewModel?.Dispose()`

## Multi Panel Behavior

- `AllowMultipleInstances => true`
- `usedIds` による panel ID 管理
- `CreateNewToolViewRequested` で新規パネルを増やす

## Dependency Risk

- `Timeline.csproj` は YMM4 本体 DLL 群へ多数の参照を持つ
  - `YukkuriMovieMaker.dll`
  - `YukkuriMovieMaker.Controls.dll`
  - `YukkuriMovieMaker.Plugin.dll`
  - `AvalonDock`, `NAudio`, `Vortice.*`, `SharpGen.*` など
- 直接依存は YMM4 バージョン更新で破綻しやすい
- `YMMProjectManager` 単体配布を維持するには optional adapter 化が必須

## Reusable vs Non-Reusable

- 参考にすべき設計:
  - `SetTimelineToolInfo` 入口
  - Scene 解決 -> `TimelineViewModel` 生成フロー
  - 再生成前 dispose とイベント解除
- 流用しない方針:
  - plugin 前提の直接埋め込み
  - `TimelineView` への直接 compile-time 依存
  - DiffTimeline への転用

## Integration Decision for preview11

- `FutureYmmTimelineAdapter` は scaffold のみ（安全に失敗）
- 実体依存は未導入
- 失敗時は `PlaceholderPureTimelineAdapter` へ fallback
- DiffTL standalone 継続を優先
