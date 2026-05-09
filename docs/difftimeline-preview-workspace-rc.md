# DiffTimeline Preview Workspace RC

## Scope

RouteA Preview Workspace is an experimental-but-usable validation workspace.

## RC Metadata

- RC Version: `RouteA-PreviewWorkspace-RC1`
- Route Identity: `RouteAStandalonePreviewWorkspaceRC`
- History-preview investigation: completed
- TimelineView integration: frozen
- Default route: disabled
- Fallback: preserved

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

- `rc-validation-summary.json`
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
- session metadata (`difftimeline-reusable-compare-sessions.json` source)

`rc-validation-summary.json` is the single-file RC integrity snapshot for later comparison/audit.

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

## Preview Release Tag Draft

- Suggested tag: `routea-preview-workspace-rc1`
- Naming rule: `routea-preview-workspace-rc<N>`
- Export naming consistency:
  - `difftimeline-export-YYYYMMDD-HHMMSS`
  - `preview-package-manifest.json`
  - `preview-readiness-report.json`
