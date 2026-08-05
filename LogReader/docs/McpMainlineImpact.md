# MCP Mainline WeezTail Impact Analysis

Last updated: 2026-08-05

## Conclusion

The MCP feature can ship in the main WeezTail executable without a sidecar and without changing normal user workflows. The no-client path adds a small idle local-pipe listener after the main window is shown, but it does not load the MCP catalog, open logs, build indexes, start tailing, poll, or create MCP-only file sessions. The final single-file executable is 1,452,616 bytes (0.89%) larger than the retained pre-MCP same-commit baseline. The compressed portable and MSI deltas are both below 0.5%.

The feature becomes operationally visible only when a user configures an MCP client. Agent requests then consume bounded disk, CPU, memory, and possibly network I/O. Interactive UI work is given priority, and MCP-only sessions are evicted more aggressively than tab-owned sessions.

## Ordinary UI Path

| Area | No configured MCP client | Running MCP client |
|---|---|---|
| Process startup | Default and unknown arguments take the existing WPF path. MCP branching occurs before `App` construction only for the exact sole `--mcp-stdio` argument. | Client starts its own headless-mode process; it does not create a second WPF app. |
| UI availability | The live endpoint starts after storage/composition are ready and the main window has already been shown. Endpoint startup is fail-soft. | Compatible clients connect automatically for the same Windows user and storage identity. |
| Idle work | Three pending asynchronous named-pipe accepts, small endpoint objects, and no polling. No catalog/log/index/tail/session work. | An idle MCP process waits on stdio and performs no request polling; live availability is reprobed only on requests. |
| File sessions/indexes | Existing tab behavior and the two-minute tab-reopen retention policy remain unchanged. | Agent reads reuse UI sessions when present. MCP-only cold sessions are capped at four/2,000,000 offsets and evicted immediately when their lease ends. |
| Scheduling | Existing UI operations are unchanged. | UI search/filter preempts live agent searches; UI tab loading cancels a competing cold agent index build; one heavy agent operation runs at a time. |
| Slow I/O | Existing UI behavior is unchanged. | Agent line reads copy bounded index offsets under a short lease, release the index lock, then perform local/UNC physical reads and revalidate generation. Slow I/O cannot hold the UI index lock. |
| Shutdown | Endpoint shuts down before the registry/viewmodel; listener failures do not block normal shutdown. | Connected/cancelled requests receive bounded cleanup; clients fall back on a later request if the UI disappears. |

## Packaging and Installation

- The host remains `WeezTail.exe`; the MSI file graph, shortcuts, install configuration, and default launch arguments do not change.
- Release configuration remains self-contained, single-file, `win-x64`, and untrimmed. The official MCP Core SDK is embedded in the existing executable.
- Retained pre-MCP executable baseline: 163,339,383 bytes. Final MCP executable: 164,791,999 bytes. Delta: 1,452,616 bytes, or 0.89%.
- Retained pre-MCP portable zip: 66,263,968 bytes. Final portable zip: 66,593,685 bytes. Delta: 329,717 bytes, or 0.50%.
- Retained pre-MCP MSI: 57,630,720 bytes. Final MSI: 57,880,576 bytes. Delta: 249,856 bytes, or 0.43%.
- Portable and MSI packaging now execute the published binary through redirected stdio and verify MCP initialize, exact tool listing, `server_status`, protocol-only stdout, clean stdin shutdown, and exit code zero.
- A running MCP client can hold the installed executable open. Release guidance tells users to restart active clients before repair, upgrade, uninstall, or portable replacement. This is normal Windows executable-lock behavior rather than an installer-specific service dependency.

## Measured UI and Client Impact

Measurements used the retained pre-MCP publish and final Release portable publish on the same Windows machine. Three alternating hidden UI launches produced a warm median time to window of 408.5 ms for baseline and 415.8 ms for final (+7.3 ms, +1.8%). The final first cold launch was 1,046.2 ms versus the retained baseline cold result of 1,129.9 ms. This is inside the investigation budget of 100 ms or 10% and does not indicate a startup regression.

After a two-second settle and one-second idle sample:

- baseline UI working set was 151.3-151.9 MB with 1,494-1,496 handles and 18 threads;
- final no-client UI working set was 153.3-153.9 MB with 1,517 handles and 20 threads;
- sampled sustained idle CPU was 0 ms for every baseline and final run.

The approximately 1.6-2.0 MB working-set, 21-23 handle, and two-thread increase is consistent with the live endpoint and its three pending asynchronous accepts. Instrumented endpoint tests separately prove those accepts do not poll or touch configured logs, catalogs, indexes, sessions, watchers, or tails.

With a running final UI, `server_status` from one and then three MCP clients confirmed `liveUi` routing. One idle client raised the UI working set from 153.9 MB to 159.1 MB and handles from 1,517 to 1,528. Three idle clients stabilized at 159.7 MB and the same 1,528 handles; UI sampled idle CPU remained 0 ms throughout. Each client process retained about 15.7-16.6 MB private memory and about 54-55 MB working set, much of which represents shared runtime/image pages. This is an opt-in cost only for configured clients and scales per client when the UI is absent or present.

The active-process replacement proof also behaved as expected: Windows blocked overwriting the exact running portable executable, stdin closure ended the MCP process with exit code 0, and the same replacement succeeded immediately afterward.

## Resource Bounds

- Live endpoint: current-user, local-machine named pipe; three client slots; 1 MiB maximum internal frame; one disk-heavy live agent request and two light requests at a time.
- Headless process: two disk-heavy operations, one UNC operation, four indexed sessions, 2,000,000 mapped offsets (16 MB of mapped `long` offsets), 30-second warm retention, 200,000 response characters, and a 30-second maximum request deadline.
- UI MCP leases: four agent-only sessions and 2,000,000 mapped offsets across them; UI-owned sessions are not evicted to make room for agents.
- Each configured MCP client owns its own process and headless cache when the UI is absent. The expected maximum is three agents, usually one.

## User-Facing Risks and Mitigations

1. **Large or network searches can compete with the UI.** Requests are bounded; UNC work is serialized; interactive search/filter and tab loading take priority.
2. **Extra memory when the UI is absent.** Each agent process owns a bounded cache. When the UI later becomes available, arbitration disposes the headless backend and its cache.
3. **Sensitive log excerpts reach an agent.** Only current dashboard members are selectable, every request reauthorizes IDs, physical paths are omitted, outputs are bounded, and documentation requires a trusted client/agent. WeezTail cannot redact domain-specific secrets already present in logs.
4. **Local cross-user or remote pipe access.** The pipe uses the current-user restriction and rejects remote clients. Its derived name binds the current user and active storage identity.
5. **Startup regression.** MCP mode uses an exact argument match; unit tests cover default and unknown-argument UI routing. The listener starts after the window is visible and is fail-soft.
6. **UI responsiveness under slow reads.** Index-lock scope excludes physical log reads; scheduling tests prove UI acquisition/preemption behavior.
7. **Upgrade file locks.** Documentation requires stopping/restarting client-owned MCP processes; there is no service or daemon to manage.

## Release Gates

Keep the one-binary design while all of the following remain true:

- executable/package growth stays small relative to the application;
- no-client UI launch and idle measurements remain within normal run-to-run variance;
- endpoint idle behavior stays free of polling and log/catalog/index work;
- one and three-client stress tests do not starve interactive search, tab load, tail, viewport navigation, or shutdown;
- full solution, published stdio, portable, and MSI validations pass.

Reconsider a sidecar executable if a future MCP SDK materially increases ordinary UI startup or idle working set, if trimming constraints diverge from the WPF app, or if independent MCP servicing becomes necessary. A persistent shared daemon remains out of scope unless measured multi-process headless cache cost justifies its lifecycle and security complexity.
