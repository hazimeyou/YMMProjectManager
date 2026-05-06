# Changelog

## v0.2.9-history-preview12

- Added `ExperimentalYmmTimelineHostWindow` and `ExperimentalYmmTimelineHostViewModel`
- Added `PureTimelineExperimentalOptions` with default-disabled experimental mode
- Added guarded experimental initialize path in `FutureYmmTimelineAdapter`
- Added dispose-safety diagnostics (`initialize/dispose/active host/failure counts`)
- Added benchmark metrics for experimental host initialize/dispose
- Updated timeline investigation and integration decision docs

## v0.2.9-history-preview11

- Added `FutureYmmTimelineAdapter` experimental scaffold
- Added `PureTimelineAdapterKind` and adapter-kind switching base
- Added fallback verification path (future adapter failure to placeholder)
- Added host UI status details (`AdapterKind`, `Fallback`, `LastError`)
- Expanded YMM4-Timeline code investigation docs
- Added third-party MIT usage notes

## v0.2.9-history-preview10

- Added Pure Timeline adapter boundary (`IPureTimelineAdapter`)
- Added `PureTimelineHostViewModel` and `PlaceholderPureTimelineAdapter`
- Added fallback-safe host behavior for Pure Timeline failures
- Added Pure Timeline fallback design doc
- Added YMM4-Timeline code investigation plan doc

## v0.2.9-history-preview9

- Timeline sync UX improvements
- CurrentFrame navigation (go/center/nearest)
- Frame jump operations (first/last/prev-from-frame/next-from-frame)
- SyncState and TimelineMode UI switching
- Pure Timeline integration checklist added
- Benchmark diagnostics for sync operations

## v0.2.9-history-preview8

- Pure Timeline investigation docs added
- ProjectDiffWindow pure timeline placeholder panel added
- Timeline sync PoC (one-way frame sync)
- DiffTimeline sync-ready architecture update

## v0.2.9-history-preview7

- Timeline UX improvements (zoom, ruler, grouping)
- Visible range filtering and benchmark expansion
