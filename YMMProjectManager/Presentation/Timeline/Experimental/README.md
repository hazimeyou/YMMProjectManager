# Presentation Structure Notes

- `Presentation/Timeline/Experimental/*` contains research-only timeline investigation UI and probes.
- `Presentation/ViewModels/*` keeps product-facing view models (DiffTimeline standalone etc.).
- Experimental flags remain default-disabled and must stay opt-in.

Safety invariants:
- `EnableExperimentalYmmTimelineHost=false`
- `AllowViewModelGenerationAttempt=false`
- Fallback path must remain preserved.
