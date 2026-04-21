# LogReader — Codex Instructions

## Validate
- After code or config changes, run:
  1. `dotnet build <target>`
  2. `dotnet test <target>`
- Run `dotnet clean <target>` first only for config, dependency, or build-system changes.
- Do not introduce new build or test failures.
- During multi-step work, keep the project buildable between major edits when practical.

## Scope
- Keep diffs minimal and localized.
- Match nearby code style and existing patterns.
- Reuse existing abstractions before adding new ones.
- Avoid user-visible UI/layout changes unless required.

## Git
- Commit only if the user asks or the task requires a committed result.
- Do not amend or push unless explicitly requested.

## Behavior
- For tasks that involve multiple steps, refactors, dependency changes, or work expected to span several edits, use PLANS.md to record and follow a short execution plan.
- Do not use PLANS.md when the user explicitly asks to use other planning/tracker markdown files; in that case, use the user-specified files instead.