# WeezTail MCP Security and Resilience Model

Status: Reviewed for v1

Last updated: 2026-08-05

## Security posture

WeezTail MCP is a local, read-only stdio process. It exposes five bounded tools, accepts configured IDs instead of arbitrary paths, does not control the UI, and does not open a network or named-pipe listener. The process runs with the Windows privileges of the MCP client that launches it.

The security goal is to let a trusted local client read bounded excerpts from the current saved dashboard membership without turning the WPF application into an agent worker. Log contents remain sensitive and untrusted.

## Trust boundaries

| Boundary | Trust decision | Controls |
|---|---|---|
| MCP client to stdio process | The user trusts the configured client to start the sidecar and receive selected excerpts. | Exact absolute `WeezTail.Mcp.exe` command with no arguments; protocol-only stdout; sanitized stderr; no shell, HTTP, token, or UI-control surface. |
| MCP DTOs | Caller input is untrusted. | Fixed schemas; typed IDs; count, character, and deadline limits; 250 ms regex match timeout; no path, URI, glob, command, or cache-name input. |
| Persisted dashboard JSON | Saved data may be malformed, stale, or concurrently replaced. | Non-writing coherent snapshot reader; 16 MiB/store and depth-64 parse bounds; current envelope only; topology, reference, count, ID, name, path, and date-pattern validation. |
| Configured file paths | Current saved dashboard membership is the authorization source. | Every operation re-reads and revalidates the snapshot, resolves typed IDs, and selects the first existing resolver-produced date candidate. Physical paths are not returned. |
| Log content and labels | Data may resemble prompts, markup, protocol frames, or terminal controls. | Structured fields; untrusted-data descriptions; control normalization; line, response, tree, file-count, hit-count, and deadline budgets. |
| Index/cache files | Private process implementation state. | Each MCP process owns a unique locked cache directory; stale owners and legacy flat indexes are cleaned without mapping or deleting an active owner's files. |
| Tail cursor | Caller-provided opaque state is untrusted. | 4,096-character cap, process-random HMAC, path/generation binding, constant-time MAC comparison, and explicit reset on rotation or truncation evidence. |
| Packaging and upgrade | The installed sidecar is executable code and may be locked by a client. | Sidecar and UI are built, versioned, and shipped together; no service or daemon; published stdio smoke test; stop client processes before replacement. |

## Authorization and account scope

The saved catalog is reloaded for every operation. A stale configured ID cannot authorize a path that is no longer in the current dashboard, and the server never writes migration, recovery, or normalization changes back to disk.

There is no separate WeezTail user identity. The Windows account that launches the MCP process must be able to resolve the selected WeezTail storage location and read the configured log files. Company-managed or special Codex accounts may need a different storage/authentication design; that cross-account workflow is explicitly deferred rather than approximated in v1.

## Resource and failure bounds

- At most 50 files and 500 returned hits per search request.
- At most 30 seconds per request and 200,000 response characters.
- At most two disk-heavy operations and one UNC operation per process.
- At most four retained indexed sessions, 2,000,000 mapped offsets, and 30 seconds of warm retention per process.
- Cancellation is propagated through MCP, query, reader, search, and index-building layers.
- File replacement, truncation, deletion, reappearance, invalid encodings, oversized lines, and incomplete final lines have regression coverage.
- Public errors use stable codes and safe messages. Diagnostics do not disclose configured paths or log contents.

## Residual risks

1. A trusted MCP client receives log excerpts, which may contain domain-specific secrets that WeezTail cannot recognize or redact reliably.
2. Several client processes can create separate bounded caches and compete for the same disk or network share. There is no cross-process scheduler or shared index in v1.
3. Windows file permissions and share credentials remain authoritative. Cross-account access is not brokered by the running UI.
4. The MCP SDK may buffer an oversized single protocol line before application-level validation; keep the SDK updated and do not expose stdio through an untrusted transport.
5. A running MCP process can hold `WeezTail.Mcp.exe` open until its client closes stdin or terminates it.

These risks are accepted for a local, explicitly configured v1 surface. Adding remote transport, arbitrary paths, mutation, shared caches, UI brokering, or a daemon requires a new security review.
