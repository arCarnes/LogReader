---
name: multi-agent-code-reviewer
description: High-signal repository code review that builds a repo map, runs specialist review passes across architecture/design, correctness/bugs, performance/scalability, testing/reliability, and maintainability/developer experience, then merges and deduplicates evidence-backed findings. Use when asked to review a repository, branch, diff, or PR for engineering risk, architectural problems, correctness bugs, reliability issues, performance concerns, test gaps, or maintainability problems in an actively developed production-oriented codebase.
---

# Multi-Agent Code Reviewer

## Run The Review

1. Build a short repo map before deep review.
2. Identify the code paths most likely to hide real risk.
3. Run specialist passes in parallel when delegation is available and appropriate.
4. Run the same specialist passes sequentially when delegation is unavailable.
5. Merge, deduplicate, and rank findings before writing the final report.

## Build The Repo Map

- Inspect the repository structure first. Identify languages, frameworks, entry points, core modules, test layout, config and infrastructure files, and build or deploy tooling.
- Read manifests, solution or workspace files, dependency files, CI configs, container files, and top-level docs before reading many source files.
- Summarize the architectural shape in 4-8 bullets so later specialists share the same mental model.
- Mark likely hotspots: central modules, heavily imported utilities, stateful services, concurrency code, adapters to external systems, large files, migration code, and areas with weak or missing tests.
- Prefer source files, tests, configs, migrations, CI workflows, container files, and infra manifests over generated artifacts.

## Run Specialist Passes

- Use these default specialties:
  - Architecture and design
  - Correctness and bugs
  - Performance and scalability
  - Testing and reliability
  - Maintainability and developer experience
- Give each specialist the repo map, the hotspot list, and the finding schema.
- Instruct each specialist to inspect the whole repo but spend most effort on files most relevant to its specialty.
- Ask each specialist for a small set of substantive findings, usually 3-8, not exhaustive lint-like notes.
- Avoid leaking other specialists' conclusions into the prompt. Share repo facts, not prior diagnoses.
- Add a dedicated trust-boundary or security pass only when the user asks for it or the repo clearly centers on auth, secrets, multi-tenancy, encryption, or untrusted input handling.

## Keep Findings High Signal

- Prefer real issues over speculative ones.
- Require concrete evidence: file path, symbol, short snippet description, and why the behavior matters.
- Avoid purely stylistic comments unless they materially affect correctness, operability, maintainability, or developer velocity.
- Prefer fewer strong findings over many weak ones.
- State the missing assumption explicitly when confidence depends on runtime context that is not visible in the repo.
- Ignore vendored, generated, and build artifacts unless they create risk through checked-in secrets, configuration, or deployment behavior.

## Merge And Deduplicate

- Merge overlapping findings by underlying issue, not by symptom.
- Preserve all relevant facets on the merged finding.
- Prefer root-cause findings over leaf-level manifestations.
- Rank by severity first, then confidence, then breadth of impact.
- Remove duplicates and narrow observations that are subsumed by a stronger finding.
- Read [references/review-rubric.md](references/review-rubric.md) before final scoring or whenever you need the detailed severity, confidence, search, and dedup rules.

## Write The Final Report

- Start with a repository summary covering stack, main subsystems, architectural shape, and overall risk areas.
- Present top findings in descending severity using this schema:

```text
[SEVERITY] Short title
- Facets: Architecture | Bugs | Performance | Testing | Maintainability
- Confidence: High | Medium | Low
- Location: path/to/file.ext - SymbolName
- Why this is a problem: ...
- Evidence: ...
- Likely impact: ...
- Recommended fix: ...
- Optional regression test: ...
```

- Follow with cross-cutting themes, then quick wins, then deep risks.
- Target 12-25 substantive findings for a medium-size repo. Do not exceed 40.
- Keep the report concise, skeptical, and evidence-driven.

## Adapt The Scope

- For full-repo reviews, spend more time on repo mapping and hotspot selection before specialist passes.
- For PR or diff reviews, inspect the touched files first, then expand into surrounding modules, interfaces, configs, and tests before scoring the change.
- When the repo is small, keep the same workflow but reduce the number of findings rather than padding the report.
- When the repo is very large, bias the specialist passes toward central paths and note where extra targeted searches would help confirm a suspicion.
