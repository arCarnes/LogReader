# WeezTail — Codex Instructions

## Validate
- Run validation only when a change may affect runtime behavior, data flow, configuration, dependencies, or public contracts.
- Validation means:
  1. `dotnet build <target>`
  2. `dotnet test <target>`
- Changes to tests or test infrastructure require relevant build/test validation. For concurrency fixes, add deterministic regression coverage where practical and repeat focused tests.
- Purely cosmetic or content-only changes do not require validation.
- Do not introduce new build or test failures.
- During multi-step work, keep the project buildable between major edits when practical.

## Test reliability
- Run async WPF/view-model tests that observe UI state inside `WpfTestHost`, including setup, assertions, and cleanup. Immediate dispatchers do not serialize background work.
- Make test doubles safe for worker-thread calls; synchronize shared counters and collections.
- Use explicit readiness signals instead of startup sleeps. Bound waits, release gates in `finally`, and drain owned work before disposing fixtures.
- Ensure cleanup runs after failed assertions. Never reuse a shared test host while an earlier action remains active; fail closed if draining times out.
- Separate headless behavior tests from real desktop smoke tests. Mark desktop-dependent tests with `Category=InteractiveDesktop`; explicitly report their exclusion from service-runner jobs.
- Interactive lanes must fail when desktop access is unavailable or no tests execute. Never silently skip required coverage.
- Fix synchronization causes rather than increasing timeouts, weakening assertions, or disabling more parallelism. Await builds before testing their output.

## Git
- After each coherent completed change, stage only relevant files and create a local commit.
- Do not amend, squash, rebase, or push unless explicitly requested.
- Before pushing many commits, ask whether to squash.

## Environment
- This is a WPF-based application being developed on a windows machine.