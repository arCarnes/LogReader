# WeezTail MCP Log Server Guide

Last updated: 2026-08-05

WeezTail includes a read-only Model Context Protocol (MCP) mode that lets a configured agent discover and query the log files saved in your dashboard tree. It exposes bounded tools, not whole log files, and it never lets an MCP caller supply an arbitrary filesystem path.

## Configure an MCP Client

Install WeezTail first, then configure the MCP client to start the installed executable with the exact, sole `--mcp-stdio` argument. Use an absolute path and provide the argument separately from the command so paths containing spaces do not depend on shell quoting or a working directory.

Generic MSI example:

```json
{
  "mcpServers": {
    "weeztail": {
      "command": "C:\\Program Files\\WeezTail\\WeezTail.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

For a portable install, replace `command` with the absolute path to `WeezTail.exe` in the portable folder. MCP client configuration keys vary by product, but the command and argument remain the same.

If an MSI install has not completed first-run storage setup for the current Windows user, launch WeezTail normally once, select the storage folder, configure the dashboard tree, close or leave the app running, and retry the MCP client. MCP mode never opens the storage picker, migrates legacy data, performs recovery, or launches the UI on your behalf.

## Process and UI Lifecycle

Each configured MCP client starts and owns one `WeezTail.exe --mcp-stdio` process. Closing the client connection or its standard input ends that process. Multiple client configurations therefore use separate MCP processes; up to three simultaneous clients can connect to one running UI endpoint.

The MCP process chooses a backend for each request:

1. If a compatible WeezTail UI is already running for the same Windows user and storage configuration, the MCP process asks that UI process to execute the read through its existing file sessions and indexes.
2. If no compatible UI is available, it reads the saved dashboard configuration and logs through a bounded, WPF-free headless backend.
3. It periodically rechecks availability only when another request arrives. It does not poll in the background, launch WeezTail, show a window, or activate an existing window.

A request stays on one backend for its duration. If the live endpoint disappears before a result is emitted, an idempotent operation may retry once through the headless backend. Live endpoint overload is reported to the caller instead of bypassing UI scheduling safeguards.

Restart active MCP clients before replacing, repairing, upgrading, or uninstalling WeezTail. A running MCP process can hold the installed executable open, just like any other running program.

## Tools

V1 advertises exactly five read-only tools:

| Tool | Use |
|---|---|
| `list_log_tree` | Discover folders, dashboards, and dashboard-member log files and obtain stable IDs. |
| `search_logs` | Search one or more typed targets using literal text or a bounded .NET regular expression. |
| `read_log_lines` | Read a bounded one-based line range from one configured log-file ID. |
| `read_log_tail` | Read the current tail or poll from an opaque, process-scoped cursor. |
| `server_status` | Inspect backend readiness, live-UI availability, limits, and bounded cache counts. |

The server does not advertise MCP Resources or Resource Templates. A resource commonly represents URI-addressable content that a client may fetch into model context; exposing a raw log as a resource could encourage an entire, arbitrarily large file to be fetched. WeezTail instead returns explicitly bounded excerpts from tools.

## Select Configured Logs

Call `list_log_tree` first and use the returned typed stable IDs:

- A `folder` recursively expands all descendant dashboards in saved tree order.
- A `dashboard` expands only its saved file membership in dashboard order.
- A `logFile` selects that current dashboard-member file only.

Names are display metadata, not authorization. Duplicate folder, dashboard, and file names are allowed; use the stable ID and `treePath` to distinguish them. A removed membership or stale ID no longer authorizes a read.

Mixed or overlapping targets form a stable first-seen union. If the same configured file is reached through several targets, WeezTail scans its normalized physical path once and retains all matched dashboard/folder paths as provenance. If expansion exceeds the effective file limit, the request fails before opening logs instead of silently searching a subset.

Date-shifted requests must supply an explicit non-negative `dateOffsetDays`. MCP mode does not inherit the dashboard date modifier currently selected in the UI.

Every result carries a schema version, request ID, backend, catalog revision, partial/truncation flags, structured errors, and effective limits. Missing, rotated, inaccessible, or timed-out files normally produce per-file errors while other selected files can still succeed. Physical local and UNC paths are omitted.

Log lines are untrusted data. A configured agent can receive the bounded excerpts returned by these tools, so only configure agents and MCP clients you trust with the content of the selected logs. WeezTail normalizes control characters and bounds line and response text, but it cannot decide whether application logs contain business-sensitive data, secrets, or personal information.

## Index Reuse and Resource Cost

The WeezTail index is a line-offset index, not a full-text search index.

- With a running UI, indexed line, tail, and search-context reads reuse the UI-owned `FileSessionRegistry` sessions and mapped line offsets. The MCP process does not copy or map another process's private cache files.
- Without a UI, each MCP process owns a separate bounded cache with at most four indexed sessions, 2,000,000 mapped line offsets, and short warm retention.
- General text search still scans selected log content. Asking for context around matches uses the line index for the surrounding line reads.

Agent operations can therefore add disk, CPU, or network traffic, especially for large folders and UNC logs. Interactive UI search/filter and tab loading have priority over agent work. The UI accepts at most three live clients and runs at most one disk-heavy agent request at a time.

## Troubleshooting

`storage_not_configured`
: Launch WeezTail normally once as the same Windows user, select storage, and retry. The MCP process intentionally fails rather than showing UI.

`migration_required` or `recovery_required`
: Launch WeezTail normally and let the interactive app migrate or recover its saved configuration. Retry after the app has opened successfully.

Live UI unavailable or incompatible
: `server_status` reports `liveUiAvailable` and `lastFallbackReason`. Headless fallback is expected when the UI is absent, belongs to another user/storage root, is still starting, is shutting down, or uses an incompatible internal protocol.

Missing, rotated, or inaccessible log
: Confirm the file is still a member of the saved dashboard and that the current Windows user can read it. Rotation can invalidate tail cursors and is returned as `generationChanged`.

UNC timeout or slow search
: Network logs are subject to the same request deadline and an additional single-UNC-operation gate. Narrow the targets or lower the requested result limits. A timeout does not authorize a longer server deadline.

Truncated result
: Inspect `isTruncated`, `truncationReasons`, per-file flags, and `effectiveLimits`. Narrow the query/targets or page `list_log_tree`; callers may lower limits but cannot raise server maxima.

Invalid tail cursor
: Tail cursors are integrity-protected and process-scoped. Omit the cursor to start again after restarting the MCP process or changing backends.

Protocol or stdout error
: Configure the executable directly, keep `--mcp-stdio` as a separate argument, and do not wrap the command in a script that writes banners or diagnostics to stdout. MCP protocol frames are the only valid stdout content.

Client will not shut down
: Close the MCP connection so the client closes the process standard input. If the client was terminated abnormally, end only its specific `WeezTail.exe --mcp-stdio` process before upgrading or replacing files.

