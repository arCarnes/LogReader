# MCP Mainline WeezTail Impact Analysis

Last updated: 2026-08-05

## Conclusion

The MCP feature remains in the main `WeezTail.exe`, but exact `--mcp-stdio` mode branches before WPF application construction. The ordinary UI path has no MCP listener, connection state, agent scheduling, shared cache, or background catalog/log work. A configured client starts a separate headless process and pays the MCP resource cost only while that process is running.

Sharing UI-owned indexes was evaluated and removed from v1. The potential cold-index savings did not justify a named-pipe protocol, backend arbitration, UI/agent ownership rules, or agent scheduling inside the user-facing process.

## Runtime impact

| Area | Ordinary WPF launch | Exact `--mcp-stdio` launch |
|---|---|---|
| Startup | Constructs and runs the existing WPF application. | Starts the WPF-free stdio host; no window or single-instance UI coordination. |
| Idle work | No MCP listener, polling, catalog reads, or MCP sessions. | Waits for stdio requests; no background polling. |
| Queries | Existing tab, search, filter, and tail behavior. | Reads a validated saved-catalog snapshot and queries configured logs headlessly. |
| Indexes | UI sessions keep their existing ownership and retention. | The client process owns a separate bounded cache and lifetime lock. |
| Shutdown | Existing WPF shutdown. | Closing stdin cancels work, disposes the cache owner, and exits cleanly. |

The processes can still contend for operating-system disk or network-share bandwidth if a user interacts with the same logs while an agent scans them. That remaining contention is bounded per MCP process but is not centrally scheduled.

## Packaging and installation

- The host remains `WeezTail.exe`; shortcuts and default launch arguments are unchanged.
- Official packages remain self-contained, single-file, `win-x64`, and untrimmed.
- Portable and MSI-payload packaging execute the published binary through redirected stdio and verify initialize, the exact five-tool surface, `server_status`, protocol-only stdout, clean stdin shutdown, and exit code zero.
- A running MCP client can hold the executable open. Stop or restart active clients before repair, upgrade, uninstall, or portable replacement.
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
5. **Upgrade file locks.** Clients must release the process before replacement; there is no service or daemon to manage.

## Release gates

- Full solution build and tests pass.
- Published portable stdio smoke passes with protocol-only stdout.
- Ordinary WPF mode still takes the default and unknown-argument routes.
- Headless measurements remain within request, memory, cancellation, and shutdown budgets.
- Package growth remains acceptable for the one-binary distribution.

Reconsider a sidecar executable if MCP dependencies materially affect package or WPF startup characteristics. Reconsider shared ownership only if measured, common multi-client workloads justify its security and lifecycle cost.
