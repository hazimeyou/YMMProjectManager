# DiffTimeline Preview Workspace RC

## Scope

RouteA Preview Workspace is an experimental-but-usable validation workspace.

## RC Checklist

- compare success
- compare blocked (same snapshot / running / invalid selection)
- compare no-op (snapshot body missing)
- restore success
- restore failure (missing session/snapshot)
- diagnostics export success
- preview package completeness
- session persistence
- manual validation log generation

## Required Export Files

- `preview-workspace-state.json`
- `snapshot-browser-state.json`
- `comparison-history.json`
- `manual-ui-validation-log.json`
- `manual-ui-validation-summary.json`
- `manifest.json`
- `preview-readiness-report.json`
- `route-validation-report.json`
- `validation-dashboard.json`
- `validation-history.json`

## Unsupported / Frozen

- TimelineView integration remains frozen.
- Runtime bridge remains frozen.
- Standalone route remains default-disabled.
- Legacy fallback remains mandatory.

## Future Work

- virtualization / rendering optimization
- large project performance tuning
- advanced semantic graph
- full snapshot persistence
- multi-session compare UX
