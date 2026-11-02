# UNC performance implementation goal prompt

Status: Ready to copy for a later execution request. This document does not activate a goal. Read the [package overview](README.md) and [resume checkpoint](ExecutionPlan.md#resume-checkpoint) before using it.

## Copyable prompt

```text
Create a goal to execute the UNC performance improvement package in
LogReader/docs/plans/unc-performance/README.md through U0-U5, including required
validation, measured accept/reject decisions, documentation and local commits.
Do not set a token budget unless I explicitly provide one.

Read the applicable AGENTS.md instructions, then Design.md, ExecutionPlan.md and
ValidationAndMeasurements.md in that directory. ExecutionPlan.md owns progress;
Design.md owns technical decisions; ValidationAndMeasurements.md owns acceptance.
Current user instructions take precedence. Reconcile actual HEAD, working tree,
existing commits and source with the checkpoint before doing new work. Preserve
unrelated user changes and do not switch branches or rewrite history.

Implement U0-U5 in dependency order. Establish reproducible baseline fixtures and
measurement tooling before optimizing. Do not depend on ignored evaluation files
or this conversation. At each coherent completed change, stage only relevant
files, create a local commit and record its evidence in the execution plan.

The intended changes are first-turn interactive scanning, a measured four-worker
UI host/share trial, topology-aware MCP admission under the existing default
global cap of two, and at most two concurrently prepared monitor files with
ordered line evaluation and state reduction. Keep the existing public contracts,
budgets, lifecycle semantics and default global disk limit. Apply the documented
candidate acceptance rule; rejected candidates retain preceding accepted runtime
behavior and get an explicit evidence record. Continue independent work.

Real UNC access is restricted to \\mint-serv\Plex Media\Test and descendants.
Never enumerate or list its parent or sibling directories. Do not follow reparse
points outside the subtree. Remote fixtures are read-only. Put synthetic writes,
indexes and results in isolated local ignored artifacts. Use fake UNC topology
and local fixtures for mutation, outages and additional hosts. Do not change SMB
configuration, permissions, signing, encryption or authentication. Do not expose
credentials, physical paths, log text or private labels in diagnostics.

Build before testing the resulting output. Run focused tests first, repeat new
concurrency tests three times, then run affected full project suites and final
solution validation. Use WpfTestHost for async UI state observations, readiness
signals instead of startup sleeps, thread-safe test doubles and fail-closed
cleanup/draining. Report service-safe exclusion of InteractiveDesktop and run the
separate real desktop lane; unavailable access, skipped or zero tests cannot pass.
Use actual measurements and keep component, end-to-end, fake topology, local and
read-only UNC evidence distinct. Missing required checks leave acceptance open.

Keep phase status, next action, commands/results, candidate decisions, surprises,
commit hashes and owned operations current in ExecutionPlan.md. If interrupted,
write a concrete resume checkpoint. Do not mark the goal complete until all
required phases are assessed, validation/measurement evidence is present,
required checks pass, owned work is cleaned up and relevant changes are committed.
Report remaining external limitations honestly and continue unaffected work.

Do not implement deferred polling backoff, watcher integration, buffer tuning,
metadata consolidation, caching/handle pools, cache relocation, rolling-date
expansion or discovery changes. Do not add dependencies without a concrete scoped
need. Do not bump versions, tag, push, publish, install or modify network security.
Resolve routine engineering choices within the design without repeated approval
requests; record material deviations and seek input only when genuinely blocked
by a decision outside the authorized scope.

Finish with changes, validation and measured outcomes, commit references and any
remaining risks. The objective is validated improvement or evidence-based
rejection of candidates, not merely completing code edits.
```

## Resume use

For an existing active goal, ask to resume that goal with the same package rather than create a duplicate. The executor must first reconcile the checkpoint and active owned work. A user pause must remain respected until the user requests resumption.
