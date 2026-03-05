# LogReader - Claude Code Instructions

## Git Workflow
- After completing any code or configuration changes, create a local Git commit before finishing the task.
- Use clear, task-focused commit messages.
- Do not amend existing commits unless explicitly requested.
- No need to mention the commit being co-authored by claude code, this will be the case for 99% of commits in this project

## Spec Requests
- When the user asks for a "spec", return it in a single copy/paste-ready Markdown code block.
- Do not include clickable file links in specs; use plain relative paths.
- Use clear sections: Objective, Scope, Required Changes, Validation, Acceptance Criteria, Commit.
- Keep specs implementation-ready: concrete file targets, explicit test commands, and measurable acceptance criteria.
