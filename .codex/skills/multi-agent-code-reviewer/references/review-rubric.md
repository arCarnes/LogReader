# Review Rubric

## Table Of Contents

1. Severity
2. Confidence
3. Deduplication
4. Specialist checklists
5. Searches that help confirm suspicion
6. Final quality bar

## Severity

- Critical: Likely production outage, data loss, security compromise, irreversible corruption, or silently wrong behavior in a critical path.
- High: Strongly likely correctness, reliability, or performance issue with broad impact, hard recovery, or poor detectability.
- Medium: Real issue with bounded scope, moderate impact, or a clear workaround.
- Low: Useful maintainability, DX, or test-gap finding that matters but is not urgent.

## Confidence

- High: Direct code evidence shows the failure mode or broken invariant.
- Medium: The evidence is strong, but one integration or runtime assumption is still required.
- Low: The concern is plausible but depends on significant unseen context. State the assumption explicitly.

## Deduplication

- Merge findings that share the same root cause even when different specialists surfaced different symptoms.
- Keep the title and narrative focused on the root issue, then list all relevant facets.
- Keep the clearest location and strongest evidence, then mention other affected paths only when they materially expand impact.
- Collapse repeated copies of the same issue unless they truly need separate fixes or represent distinct failure modes.
- Prefer one strong finding with multiple facets over several narrow findings that all say the same thing.

## Specialist Checklists

### Architecture And Design

- Look for layering violations, circular dependencies, hidden shared state, config sprawl, duplicated abstractions, and modules with unclear ownership.
- Check whether business logic lives in transport, controller, UI, or persistence layers.
- Flag places where common future changes will be unusually expensive because boundaries are blurry or responsibilities are mixed.

### Correctness And Bugs

- Look for invalid state transitions, partial updates, stale caches or derived state, bad assumptions, edge cases, silent failures, and inconsistent validation.
- Pay extra attention to parsing, serialization, retries, timeouts, nullability, concurrency, and branch conditions that depend on ordering.
- Prefer findings where the broken behavior can be explained from the code path itself.

### Performance And Scalability

- Look for repeated I/O, N+1 access patterns, heavy work in loops, blocking work on critical paths, large payload processing, needless recomputation, and memory retention.
- Pay extra attention to request paths, render paths, background processing loops, and bulk import or export flows.
- Distinguish between hypothetical inefficiency and a likely hotspot with real scaling impact.

### Testing And Reliability

- Look for important branches without tests, brittle mocks, weak assertions, test gaps around stateful or async flows, and weak observability in critical failure paths.
- Flag risky deployment or migration behavior when rollback or recovery looks unclear.
- Prefer missing-test findings that connect to a concrete risky behavior in production code.

### Maintainability And Developer Experience

- Look for files that do too much, long functions with mixed responsibilities, dead code, misleading comments, inconsistent conventions, and setup or tooling friction.
- Prefer issues that make future defects more likely or slow safe iteration.
- Avoid naming-only nits unless the naming actively obscures behavior or ownership.

## Searches That Help Confirm Suspicion

- Search for TODO, FIXME, HACK, skipped tests, broad exception handling, swallowed errors, retries, timeout settings, cache invalidation logic, shared mutable state, and ad hoc parsing.
- Search for the most imported symbols, the largest files, and modules touched by many tests to find central code paths quickly.
- Search both production code and tests when a suspected bug may already have partial coverage or contradictory assumptions.
- Tailor search patterns to the repo's stack instead of forcing language-specific heuristics that do not fit.

## Final Quality Bar

- Do not flood the report with tiny observations.
- Do not include praise unless it provides context for a finding boundary.
- Do not invent certainty. If a finding depends on a runtime assumption, name it.
- Prefer fewer findings that a staff engineer would keep in the final report after a merge review.
