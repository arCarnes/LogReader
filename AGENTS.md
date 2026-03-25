# LogReader — Codex Instructions

## Workflow

### Build & Test
- Clean, build, and run tests after changes that involve modifying actual project code. Creating a markdown, adding docs, etc do not require this step.
- Clean, build, and run tests after changes.
- All tests should pass, failing tests are not acceptable and should be addressed immediately.

### Git
- After completing any code or configuration changes, create a local Git commit before finishing the task.
- Use clear, task-focused commit messages.
- Do not amend existing commits unless explicitly requested.
- Do not ask to commit anything that is already included in .gitignore
- If asked to advance the version of the product as part of a change, the commit following such a completed request should git tag with that version.

## Code Style

### Consistency
- Match the existing codebase style in any file you edit.
- Follow established naming, formatting, project structure, and test patterns already present in nearby code.

### Reuse
- Reuse existing abstractions and libraries before introducing new patterns or dependencies.

### Diffs
- Keep diffs stylistically minimal and consistent with surrounding code.