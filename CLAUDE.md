# LogReader — Claude Code Instructions

## Validate
- After code or config changes, run:
  1. `dotnet build <target>`
  2. `dotnet test <target>`
- Run `dotnet clean <target>` first only for config, dependency, or build-system changes.
- Do not introduce new build or test failures.

## Scope
- Keep diffs minimal and localized.
- Match nearby code style and existing patterns.
- Reuse existing abstractions before adding new ones.
- Avoid user-visible UI/layout changes unless required.

## Git
- Create a local commit only if the user asks or the task requires a committed result.
- Do not amend or push unless explicitly requested.