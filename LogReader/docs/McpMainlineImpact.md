# MCP Mainline WeezTail Impact Analysis

Last updated: 2026-08-05

## Conclusion

The MCP feature ships as the dedicated `WeezTail.Mcp.exe` sidecar. `WeezTail.exe` keeps its generated WPF entry point and does not reference `LogReader.Mcp` or the MCP SDK. Both executables are built, versioned, installed, and upgraded together, while each MCP client starts its own headless sidecar process.

Sharing UI-owned indexes was evaluated and removed from v1. The potential cold-index savings did not justify a named-pipe protocol, backend arbitration, UI/agent ownership rules, or agent scheduling inside the user-facing process.

## Runtime impact

| Area | `WeezTail.exe` | `WeezTail.Mcp.exe` |
|---|---|---|
| Startup | Constructs and runs the WPF application. | Starts the WPF-free stdio host directly. |
| Dependencies | Core, infrastructure, and desktop UI dependencies. | Core, infrastructure, and the MCP SDK; no App or WPF reference. |
| Idle work | No MCP listener, polling, catalog reads, or MCP sessions. | Waits for stdio requests; no background polling. |
| Queries | Existing tab, search, filter, and tail behavior. | Reads a validated saved-catalog snapshot and queries configured logs headlessly. |
| Indexes | UI sessions keep their existing ownership and retention. | The client process owns a separate bounded cache and lifetime lock. |
| Shutdown | Existing WPF shutdown. | Closing stdin cancels work, disposes the cache owner, and exits cleanly. |

The processes can still contend for operating-system disk or network-share bandwidth if a user interacts with the same logs while an agent scans them. That remaining contention is bounded per MCP process but is not centrally scheduled.

## Packaging and installation

- Official portable and MSI packages include both single-file, self-contained, `win-x64` executables beside one `WeezTail.install.json`.
- The desktop app and sidecar share product version metadata and are released as one package.
- Packaging drives `WeezTail.Mcp.exe` through redirected stdio and verifies initialize, the exact five-tool surface, `server_status`, protocol-only stdout, clean stdin shutdown, and exit code zero.
- A running MCP client can hold only `WeezTail.Mcp.exe` open. Stop or restart active clients before repairing, upgrading, uninstalling, or replacing the portable package.
- The Windows account launching the client must resolve WeezTail's selected storage and read the configured logs. Cross-account company-managed execution is deferred pending its eventual account model.

Artifact sizes and repeatable active-request measurements are recorded in [MCP Performance and Mainline Measurements](./McpPerformanceMeasurements.md).

## Resource bounds

Each MCP client owns a process with these independent limits:

- two disk-heavy operations and one UNC operation;
- four retained indexed sessions and 2,000,000 mapped offsets;
- 30-second warm retention and 30-second maximum request duration;
- 50 searched files, 500 returned hits, and 200,000 response characters.

Multiple clients multiply those bounded process resources. A shared daemon or cross-process index could reduce duplication, but would reintroduce discovery, authentication, lifecycle, cleanup, and concurrency complexity. It is not warranted until real multi-client measurements show the separate-process model is a product problem.

## User-facing risks and mitigations

1. **Large or network searches can compete with interactive use.** Work is bounded, UNC access is serialized within each process, and client-side cancellation propagates through file operations.
2. **Several clients can duplicate memory and index work.** Each cache is capped and process-owned; documentation recommends one configured client unless parallel clients are necessary.
3. **Sensitive excerpts reach the agent.** Only current dashboard members are selectable, IDs are reauthorized for every operation, paths are omitted, and results are bounded.
4. **Different Windows accounts may see different storage or file permissions.** The MCP process uses its launching account; no UI broker or implicit privilege transfer exists.
5. **Sidecar upgrade locks.** Clients must release `WeezTail.Mcp.exe` before it can be replaced; the desktop executable is not held by MCP clients.

## Release gates

- Full solution build and tests pass.
- Published portable sidecar smoke passes with protocol-only stdout.
- Ordinary WPF startup remains generated and has no MCP dependency.
- Headless measurements remain within request, memory, cancellation, and shutdown budgets.
- Combined package growth remains acceptable for the two-binary distribution.

Reconsider shared ownership only if measured, common multi-client workloads justify its security and lifecycle cost.
