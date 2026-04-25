# LogReader — Codex Instructions

## Validate
- Run validation only when a change may affect runtime behavior, data flow, configuration, dependencies, or public contracts.
- Validation means:
  1. `dotnet build <target>`
  2. `dotnet test <target>`
- Purely cosmetic or content-only changes do not require validation.
- Do not introduce new build or test failures.
- During multi-step work, keep the project buildable between major edits when practical.

## Git
- After each completed change, run `git add` and create a local commit only after the full requested task is complete.
- Do not amend or push unless explicitly requested.
- If asked to push a large range of commits, ask the user if squashing is appropriate.