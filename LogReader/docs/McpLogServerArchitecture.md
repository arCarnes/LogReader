# WeezTail MCP architecture

The 2.0 cutover exposes eleven tools and envelope version 5. The current public contract is documented in [McpLogServerGuide.md](McpLogServerGuide.md).

## Process and authorization boundary

WeezTail.Mcp.exe is a WPF-free, read-only stdio executable. Each client launches a separate process with the launching account's filesystem access and selected saved storage. No UI broker, network listener or arbitrary-path request surface exists. Stdout carries only protocol frames; sanitized startup diagnostics go to stderr. Application, launchers and shared libraries ship together from the same version.

## MCP boundary

McpRequestContracts contains explicit wire DTOs and closed object input schemas. McpV2Tools registers only the eleven public operations. MIT-licensed JsonSchema.Net 7.3.4 validates the exact published schema before deserialization and backend invocation; domain validators enforce effective limits and cross-field/state rules. Unknown names, missing selectors, unsupported target kinds and explicit nulls are rejected with bounded actionable issues. Input schemas have no root combinators requiring client rewriting.

McpV2Response projects Core evidence through the existing sparse-bucket/shared-provenance transformations into a dedicated version-5 envelope. It centralizes request/file issues, maps completion to scanComplete, and consistently sets status and MCP isError. Core envelope versions and internal projection regression fixtures are not public tool contracts. Old tail/presentation workflows remain internal test/backend mechanisms only, with no public tool aliases.

## Bounded scans and continuations

The headless backend captures each file's byte extent at first open. Observed growth makes captured bytes unverifiable because file identity and length cannot distinguish an append from a rewrite followed by growth. The scan can finish the captured extent and return partial evidence. Detected incompatible changes stop the affected file. Files are opened individually, so each extent corresponds to its own capture time.

The process-owned continuation store retains original serialized query options and owns their memory accounting alongside traversal state and replay results. Cursor-only resume checks operation identity, current catalog authorization, expiry and revision under the existing session gate. A replay does not advance twice. Relative windows and selected rolling dates stay frozen. Expiry or lost continuity never silently starts a replacement investigation.

Line reads capture bounded indexed offsets in one lease. Start-relative and end-relative ranges resolve against the same index snapshot; independent reads do not create observation state. Monitoring captures current ends or explicitly copied verified snapshot boundaries, then advances through fair bounded caller-driven polls. Each observer stays on its selected rolling date. Unsafe changes stop affected files instead of silently rebasing their history.

## Resource and security invariants

Preserve topology-aware disk admission, heavy-request bounds, response/text/provenance limits, candidate caps, continuation count/memory/expiry bounds, and cache ownership. Shared tables reconstruct retained provenance without changing authorization. Sparse grids retain actual timestamp boundaries, including daylight-saving transitions. No exception detail, raw physical path or supplied value is added to protocol diagnostics.

## Verification

Protocol tests assert the exact public tool set, argument rejection before backend work, output-schema validity and text/structured parity. Backend tests cover frozen-extent evidence after growth, retained query state, replay, concurrency, expiry, authorization, safe handoff and end-relative physical reads. Packaging smoke drives the published executable. Acceptance also requires fresh Claude Code and Codex sessions against isolated fixture logs, including continuously appending search input; client-reported schema conversion is not established by model narration alone.

Historical consistency and performance reports remain evidence for their recorded versions, not current schema specifications. See DeveloperGuide.md for build/test lanes and the separate InteractiveDesktop category.

The JSON validator dependency notices are embedded in WeezTail.Mcp as McpDependencyNotices.txt so single-file packages retain them. Live-client acceptance may remain pending locally; a source version/tag does not publish or approve a release.
