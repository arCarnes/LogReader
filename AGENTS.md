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
- Use native `git` commands for Git operations (status, diff, branches, commits, tags, fetch, and push). Use the `gh` CLI for GitHub operations (pull requests, issues, checks, workflows, and releases).
- When GitHub CLI authentication is required, use `gh auth login --web` and share the login URL and one-time device code with the user. Never expose access tokens or other stored credentials.
- After each coherent completed change, stage only relevant files and create a local commit.
- For every application version bump, create an annotated `v<version>` tag at the version-bump commit after relevant validation passes. Verify it against `LogReader/Directory.Build.props`. For the shared launcher version line, use an annotated `launchers-v<version>` tag matching `LogReader/Launcher.Version.props`. Never move or overwrite an existing version tag.
- Bump the launcher version when launcher code, dependencies, or bundled resources change. Application-only changes do not bump it. Both launcher executables and their shared library use the same launcher version.
- Application packages must reuse the published launcher release pinned by `BundledLauncherVersion`; never rebuild launchers as a fallback or select latest. Publish the initial launcher release before an application release that pins it. Launcher releases use `--latest=false`; application release notes identify their bundled launcher version.
- When explicitly asked to push a version bump, push its version tag along with the commit and verify both remote targets. Creating a version tag does not by itself authorize pushing or publishing a GitHub release.
- Do not amend, squash, rebase, or push unless explicitly requested.
- Before pushing many commits, ask whether to squash.

## Environment
- This is a WPF-based application being developed on a windows machine.