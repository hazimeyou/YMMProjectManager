# YMM Project Manager

YukkuriMovieMaker support tool.

Main features:

- Media relink support
- Thumbnail generation
- `.ymmp` history/diff (preview)

## History/Diff (Preview)

History/Diff is experimental and may change in internal model/UI/spec.

Current preview capabilities include:

- Snapshot create/list/delete
- Snapshot vs snapshot diff
- Current vs snapshot diff
- JSON diff + semantic diff (`Text / FilePath / Frame / Layer / Length`)
- Internal Item ID PoC
- DiffTimeline prototype and navigation
- PureTimeline adapter boundary and fallback
- FutureYmmTimelineAdapter scaffold
- Experimental isolated host PoC (default disabled)
- Reflection-based timeline probe (preview13)
- `ExperimentalReady` state for future adapter (preview13)
- Timeline constructor binding dry-run (preview14)
- Generation readiness score (preview14)
- Disabled-by-default experimental generation investigation (preview14)
- Isolated TimelineViewModel generation attempt PoC (preview15)
- Immediate dispose verification after generation attempt (preview15)

### Experimental Mode Safety

- `EnableExperimentalYmmTimelineHost = false` by default
- `AllowViewModelGenerationAttempt = false` by default
- Experimental failures are treated as normal failures
- DiffTL standalone must remain usable

## Benchmarks

Run:

`dotnet run --project YMMProjectManager.Benchmarks/YMMProjectManager.Benchmarks.csproj`

Outputs:

- `logs/benchmarks/benchmark-yyyyMMdd-HHmmss.md`
- `logs/benchmarks/correctness-yyyyMMdd-HHmmss.json`

## License

MIT License

- Runtime environment detection (preview16)
- YMM4 plugin runtime reflection probe actions (preview16)
- Diagnostics output with runtime kind (preview16)

