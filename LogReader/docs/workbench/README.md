# Workbench

`docs/workbench` is a repo-visible place for developer working notes that should stay local by default.

Only the shared structure is tracked in git:

- this `README.md`
- `docs/workbench/.gitignore`
- `.gitkeep` placeholders for each bucket

Everything else created inside the bucket folders is ignored by default so each developer can keep private planning notes, scratchpads, and whiteboarding docs in the repo without committing them.

If a note should become shared documentation, move it into a tracked docs location or intentionally force-add it.

## Buckets

- `bug-fixes`: debugging notes, repro steps, and fix planning
- `code-review`: review findings drafts, comparison notes, and follow-up scratchpads
- `explorations`: design spikes, tradeoff notes, architecture whiteboarding, and open-ended investigations
- `features`: feature planning, decomposition, and implementation scratchpads

## Naming

Use `YYYY-MM-DD-topic-slug.md` for local markdown notes, for example:

- `2026-03-29-scope-tab-architecture.md`

This keeps notes sortable by date while staying easy to search by topic.
