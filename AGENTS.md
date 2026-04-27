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
- After each coherent completed change, stage only relevant files and create a local commit.
- Do not amend, squash, rebase, or push unless explicitly requested.
- Before pushing many commits, ask whether to squash.

## Plans
- When using plan mode, persist generated plans to the appropriate workbench folder.
- If no workbench folder exists, ask where plans should be saved.