# LogReader — Codex Instructions

## Workflow

### Build & Test
- After changes that modify project code or configuration, run validation as three separate blocking steps in this exact order:
  1. `dotnet clean ...` and wait for completion.
  2. `dotnet build ...` and wait for completion.
  3. `dotnet test ...` and wait for completion.
- Do not run these commands in parallel, in a single combined command, in the background, or in a way that overlaps execution.
- All tests must pass; failing tests are not acceptable and should be addressed immediately.


### Git
- After completing any code or configuration changes, create a local Git commit before finishing the task.
- Use clear, task-focused commit messages.
- Do not amend existing commits unless explicitly requested.
- Do not ask to commit anything that is already included in .gitignore

## Code Style

### Consistency
- Match the existing codebase style in any file you edit.
- Follow established naming, formatting, project structure, and test patterns already present in nearby code.

### Reuse
- Reuse existing abstractions and libraries before introducing new patterns or dependencies.

### UI Safety
- Treat visual/layout changes as risky unless explicitly required.
- If implementation would introduce user-visible changes beyond the requested behavior, call them out before making them and keep them minimal.

### Diffs
- Keep diffs stylistically minimal and consistent with surrounding code.