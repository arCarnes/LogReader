# WeezTail MCP Security and Resilience Model

Status: Reviewed for v1  
Last updated: 2026-08-05

## Security posture

WeezTail's MCP mode is a local, read-only data-access feature. It intentionally minimizes scope: stdio transport, five bounded tools, no arbitrary path parameters, no mutation, no Resources, no prompts/sampling/tasks, no network listener, no authorization-token handling, and no UI control. It runs with the same Windows privileges as the MCP client that starts it.

The security goal is not to make log content safe or public. The goal is to ensure a configured local client can read only bounded excerpts from the current saved dashboard membership, through the same Windows account that could already read those files, without turning the running UI into a remotely reachable or unbounded worker.

## Trust boundaries

| Boundary | Trust decision | Controls |
|---|---|---|
| MCP client to stdio process | The user trusts the configured client to start installed WeezTail and receive selected log excerpts. | Exact absolute command plus sole `--mcp-stdio`; stdio only; protocol-only stdout; generic stderr failure; no shell; no HTTP/auth/token surface. |
| MCP DTOs | Caller input is untrusted. | Fixed schemas; typed IDs; field/count/character/deadline limits; .NET regex 250 ms match timeout; no path/URI/glob/command/cache/pipe-name input. |
| Persisted dashboard JSON | Saved data may be malformed, stale, concurrently replaced, or intentionally hostile. | Non-writing coherent snapshot reader; 16 MiB/store and depth 64 parse bounds; current envelope only; topology, referential, count, depth, ID/name/path/date-pattern limits; generic catalog errors. |
| Configured file path | The saved current dashboard membership is the authorization source. | Every backend request re-reads/revalidates a snapshot and resolves typed IDs; selected date candidates must be members of the resolver-produced candidate set; paths never serialize. |
| Log content and configured labels | Untrusted data that may resemble prompts, protocol frames, markup, or control sequences. | Structured fields; tool descriptions label data untrusted; control normalization for log lines; line/response/tree budgets; client-facing risk annotations. |
| MCP process to running UI | Same-user local optimization, not an authentication replacement. | Current-user named pipe, native remote-client rejection, user/storage-bound derived identity, v1 handshake/capabilities, 1 MiB frames, three slots, cancellation, deadlines, safe errors. |
| Index/cache files | Private process-owned implementation state. | Unique owner directory and held lifetime lock; owner-scoped cleanup; no cross-process mapping/deletion; bounded UI/headless sessions and offsets. |
| Tail cursor | Untrusted opaque caller value. | 4,096-character cap, process-random HMAC, protected path/generation binding, constant-time MAC verification, backend-switch reset, rotation/truncation evidence. |
| Packaging/upgrade | Installed binary is executable code and can be locked by a running client. | Same signed/reviewed executable path as UI; no service/daemon/port; published protocol smoke; stop/restart clients before replacement. |

## Authorization and path audit

The only public selectors are `{ kind, id }`, `rootGroupId`, and `fileId`. The SDK-independent contracts have no physical path, storage-root, candidate-path, glob, URI, command, pipe-name, or cache-path input. Unknown JSON properties cannot become backend path fields.

For every tree/search/read/tail call, the selected backend obtains a current immutable saved snapshot and resolves the original configured ID. A dashboard must still contain the file ID; catalog-only and removed IDs fail. A folder expands current descendant dashboards. Arbitration retries pass the original ID request to the new backend, which performs its own snapshot read and authorization; no resolved physical path crosses the retry boundary.

Persisted paths are normalized with `Path.GetFullPath`. Date shifting starts from that persisted path and saved patterns; the candidate selector must return one of the normalized resolver-generated candidates. Dot segments and Windows case variants deduplicate. A caller cannot use alternate separators, trailing dots/spaces, device syntax, an alternate data stream, or a UNC string because the caller supplies no path.

Saved paths can themselves be UNC, device-like, alternate-stream, or reparse-point paths because WeezTail already permits a user to configure/import such a path with the UI's trust workflow. MCP treats the configured path as the authorization object. If an administrator or user retargets a junction/symbolic link after saving it, Windows may resolve the same configured path to a different object. V1 does not attempt unreliable cross-volume/UNC canonical-handle resolution; this is a residual configured-path trust risk, not an arbitrary caller-path escape. Physical paths and storage roots remain absent from results and normal errors.

## Bounds

Application request limits are enforced before or during acquisition:

- 50 targets and 50 resolved physical files;
- 500 stable configured IDs and 500 provenance entries during expansion, with whole-request rejection on overflow;
- 256 characters per configured/caller ID, 1,024 per display name, 8,192 per tree path, 32,767 per persisted/effective path, and 256 per timestamp input;
- 5,000 dashboard nodes, depth 100, 50,000 catalog files, and 100,000 saved memberships;
- 32 date patterns with 4,096-character fields; replacement output is size-estimated before allocation;
- 500 tree nodes per page plus a 100,000-character tree response budget;
- 4,096 query characters, 50 hits/file, 500 total hits, 4,096 characters/line, 20 context lines/side, 1,000 read lines, and 200,000 log-response characters;
- 30-second maximum request deadline, two headless disk operations, one headless UNC operation, and one heavy live agent operation;
- four retained indexed sessions and 2,000,000 mapped offsets in a headless or agent-only UI cache;
- 1 MiB live IPC request/response frames, three clients, 64-character ASCII internal request IDs, and JSON depth 64.

The live server serializes a response for a size preflight. If defensive or future backend behavior still exceeds 1 MiB after application limits, the server returns a small non-retrying `response_size_limit` envelope. It does not drop a response, hang the client, or repeat already-completed disk work headlessly.

## MCP-specific review

The 2026-08-05 review checked the current official [MCP security best practices](https://modelcontextprotocol.io/docs/tutorials/security/security_best_practices), [tool specification](https://modelcontextprotocol.io/specification/draft/server/tools), and [stdio debugging guidance](https://modelcontextprotocol.io/docs/tools/debugging).

- Local-server guidance favors stdio over an exposed local HTTP listener; WeezTail is stdio-only.
- The client/operator must consent to and trust the exact local executable command. Documentation shows the absolute installed command and separate argument without a shell or installer command.
- Tool annotations are risk hints, not access controls. All tools are `readOnly=true`, `destructive=false`, and `idempotent=true`. Search/line/tail are `openWorld=true` because configured UNC/dynamic logs can return external untrusted content; local tree/status are `openWorld=false`.
- Tool names are unique, case-sensitive, valid ASCII identifiers, and under the recommended length.
- V1 does not declare tasks, resources, prompts, sampling, elicitation, completion, HTTP, authorization, or logging capabilities beyond the low-level SDK's required protocol behavior.
- Stdout is reserved for JSON-RPC. The SDK transport logger is disabled; application startup failure uses one generic stderr line.

## Diagnostics and disclosure

Normal envelopes expose a random request ID, categorical error code, bounded generic message, retryability, stable configured IDs, configured display names/tree paths, and catalog hash. They do not expose physical paths, storage roots, Windows usernames/SIDs, pipe names, storage identities, query strings, log text in error messages, exception types/stacks, credentials, or cursor keys.

The UI listener diagnostic callback receives fixed event codes only. Live protocol errors use fixed codes/messages. Headless file exceptions map to categorical missing/access/capacity/unstable/read errors. Catalog topology failures are deliberately generic so a malicious saved ID cannot smuggle a path-like or credential-like value into an error.

## Adversarial coverage

Focused tests cover:

- catalog-only/removed/unknown/kind-mismatched IDs, malicious candidate selection, path normalization/deduplication, and retry reauthorization;
- cycles, excessive depth/nodes/files/memberships, missing references, duplicate IDs, oversized IDs/names/paths/patterns/timestamps, expansion/provenance overflow, and date replacement allocation bounds;
- malformed/legacy/future/null/deep/concurrently replaced stores without migration or mutation;
- invalid/oversized/partial/deep pipe frames, handshake/version/storage mismatch, remote rejection, four-client pressure, disconnect/cancellation, and oversized server response handling;
- invalid regex, caller cancellation/deadline, missing/locked/rotated/truncated/unterminated logs, line/response/index limits, deterministic ordering, concurrency gates, and disposal;
- tampered/cross-process/stale tail cursors and backend-switch resets;
- injection-like log lines, Unicode/control normalization, path/error redaction, unexpected backend exceptions, malformed MCP arguments, unknown tools, stdout purity, and stdin shutdown;
- default/unknown UI argument routing, fail-soft listener startup, idle listener zero-log-work behavior, UI work preemption, slow-read index-lock release, and shutdown order.

## Residual risks

- A malicious configured MCP client already runs with the user's privileges. Stdio isolates the server from unrelated network callers but cannot sandbox the client or prevent the client from retaining/exporting returned excerpts.
- The official SDK parses a stdio JSON-RPC message before application DTO limits run. A deliberately enormous line can consume memory in that client-owned MCP process. It does not enter the UI pipe because that layer has a 1 MiB frame limit. A future SDK-provided stdio message cap should be adopted when available.
- The server does not redact domain-specific secrets or personal data from log content or configured display labels. Users must restrict dashboard membership and client/agent trust accordingly.
- General search is sequential content I/O and can contend with log producers, disks, or UNC servers despite deadlines and scheduling priority.
- Configured reparse/device/alternate-stream semantics remain the responsibility of the user who saved/imported the path.
- Current-user pipe isolation assumes the Windows account is the local trust boundary. Another process already running as that same user can attempt pipe discovery/use; storage identity, protocol validation, bounds, and read-only authorization limit its effect but do not provide per-client authentication.

