# WeezTail MCP Server: Getting Started

Contract updated: 2026-11-19 (WeezTail 2.0; validate both clients before release)

WeezTail includes a local, read-only Model Context Protocol (MCP) server named `WeezTail.Mcp.exe`. It lets a technical user ask an MCP-capable agent to discover and search logs already represented in the saved WeezTail dashboard tree.

The server is a Windows x64 stdio executable. It does not open a port, start the WeezTail UI, accept arbitrary file paths, or modify logs or saved configuration. Each MCP client starts and owns a separate server process.

## Getting started

### Open MCP help from WeezTail

In the desktop app, select **MCP Server** from the main toolbar. The window shows:

- whether `WeezTail.Mcp.exe` is present beside the running app;
- whether normal WeezTail storage startup completed;
- how many saved dashboards are represented in the loaded tree;
- the exact server path to copy into an MCP client;
- a concise offline explanation of setup, agent behavior, and technical boundaries.

This is a local setup summary, not a live MCP health check. Opening it does not start, connect to, or send requests to the sidecar. **Open full guide** opens this detailed guide in the default browser; network and repository access may be required.

### Before you connect a client

1. Install WeezTail from the MSI or extract the Portable or AppData ZIP package.
2. Start `WeezTail.exe` under the same Windows account that will run the MCP client.
3. Complete storage setup if prompted (MSI), and save at least one dashboard containing a configured log file. The AppData ZIP automatically uses `%LOCALAPPDATA%\WeezTail`.
4. Confirm that account can read the configured local or UNC log files.
5. Note the absolute path to `WeezTail.Mcp.exe`.

The default MSI path is:

```text
C:\Program Files\WeezTail\WeezTail.Mcp.exe
```

For a ZIP package, use the executable in the extracted directory. Always configure the absolute path and do not add command-line arguments.

For shared-drive use, extract the **AppData ZIP** and point each client's command to a UNC path such as `\\server\tools\WeezTail\WeezTail.Mcp.exe`. Each process runs on the client's machine using the launching account's `%LOCALAPPDATA%\WeezTail` configuration. Users need read/execute access to the package and read access to configured logs. Mapped-drive paths also work when that drive is available in the launching session. Close all desktop and MCP processes before replacing the shared executables. See the [Installation Guide](./InstallationGuide.md#appdata-zip-and-shared-drives).

### Add WeezTail to Codex

Codex supports local stdio MCP servers and shares its MCP configuration between the ChatGPT desktop app, Codex CLI, and Codex IDE extension on the same host. The current options are documented in the [official Codex MCP guide](https://developers.openai.com/codex/mcp).

#### Command line

Run this in PowerShell:

```powershell
codex mcp add weeztail -- "C:\Program Files\WeezTail\WeezTail.Mcp.exe"
```

For a portable install, replace the executable path with its absolute path.

Verify the configuration:

```powershell
codex mcp list
```

Start or restart Codex, then enter `/mcp` in a Codex session to confirm that `weeztail` is connected and exposes eleven tools.

#### ChatGPT desktop app or Codex IDE extension

1. Open **Settings** (or the IDE extension's gear menu) and select **MCP servers**.
2. Select **Add server**.
3. Name the server `weeztail` and choose **STDIO**.
4. Set the command to the absolute `WeezTail.Mcp.exe` path and leave arguments empty.
5. Save, then restart the app or extension when prompted.

The graphical setup writes the same Codex MCP configuration used by the CLI.

### Add WeezTail to Claude Code

Claude Code supports local stdio MCP servers. Its current command syntax and configuration scopes are documented in the [official Claude Code MCP guide](https://code.claude.com/docs/en/mcp).

To make WeezTail available to Claude Code across your projects, run this in PowerShell:

```powershell
claude mcp add --transport stdio --scope user weeztail -- "C:\Program Files\WeezTail\WeezTail.Mcp.exe"
```

For a portable install, replace the executable path with its absolute path. If you want the configuration only in the current project, omit `--scope user`; Claude Code uses local scope by default.

Verify the configuration:

```powershell
claude mcp get weeztail
claude mcp list
```

Start or restart Claude Code, then enter `/mcp` in a session to confirm that `weeztail` is connected and exposes eleven tools.

## How agent log access works

Ask the agent to use the saved hierarchy, for example: "Using WeezTail, search env1 > app1 > instance1 for ERROR and summarize the evidence."

The agent discovers IDs with list_log_tree, explores relevant branches, and supplies nonempty typed targets to search_logs or count_logs. Search supplies examples; count supplies totals. Both capture each file's extent when opened and exclude later bytes. If a file grows during the scan, the agent finishes the existing continuation and reports the evidence with the integrity caveat. Growth alone does not call for another search or an observation.

Read the latest physical lines with read_log_lines using anchor=end and count. This also works for untimestamped stack traces. Time-window search instead selects events with recognizable timestamps. There is no separate tail tool.

Observation requires an explicit request such as "Observe new ERROR lines for one minute, then stop and summarize." The agent uses start_log_monitor, poll_log_monitor and stop_log_monitor. Initial observation captures ends without historical scanning. An explicitly requested snapshot handoff uses enableHandoff followed by start_log_monitor_from_snapshot.

Version 2.0 is a cutover: refresh discovery through a restarted server connection. The eleven-tool interface rejects unknown fields and explicit nulls. Search/count continuation tools accept only cursor, timeoutMilliseconds and includeStatistics. Responses use envelope version 5 with status, data, centralized issues and continuation. Check scanComplete for verified captured-scope completion and issues for qualifications. Normal pagination is successful.

See the [server guide](McpLogServerGuide.md) for exact operations, recovery semantics, examples and limits.

## Technical reference

### Runtime and security notes

- The MCP client receives selected log excerpts. WeezTail cannot reliably redact application secrets or personal data stored in those logs.
- Log text and saved display labels are untrusted data, not agent instructions.
- Every request revalidates that the selected ID is still present in the saved dashboard tree.
- Results never expose physical log paths or WeezTail storage roots.
- Searches and reads have fixed limits for files, hits, lines, response size, concurrency, and elapsed time. Partial or truncated results are expected when a limit is reached.
- Tail cursors belong to the server process that created them and become invalid after the client or server restarts.
- Search cursors are also process-scoped, bind the complete normalized request and catalog revision, and reauthorize configured membership on every page. Search and count cursors resume within files and across configured files, expire after 15 minutes idle, and become invalid after restart. Hit limits apply per response; count results are cumulative replacements.
- Close or restart MCP clients before upgrading, repairing, uninstalling, or replacing WeezTail so they release `WeezTail.Mcp.exe`.

### Troubleshooting

#### The server is not listed or will not connect

- Confirm the configured command is the absolute path to `WeezTail.Mcp.exe`.
- Confirm no arguments were configured.
- Confirm the executable exists and is from an installed or portable packaged build.
- Restart the client after changing its configuration.
- In Codex or Claude Code, enter `/mcp` to inspect connection status.

#### `storage_not_configured`

Start the normal WeezTail UI under the same Windows account, complete storage setup, and restart the MCP client.

#### `migration_required` or `recovery_required`

Start the normal WeezTail UI and let it migrate or recover the saved stores. The MCP server intentionally never mutates them.

#### `log_access_denied`

Confirm that the Windows account running Codex or Claude Code can read the configured local or UNC file. Access held by a WeezTail UI process running under another account is not transferred to the MCP process.

#### Results are partial or truncated

Follow nextCursor for ordinary work or output limits. When no cursor remains, check isTraversalComplete and the count exactness/completion flags. Review file errors and generation uncertainty; narrow the scope for oversized lines or continuation capacity errors. A text search that reaches the query-wide hit ceiling terminates with query_hit_limit and no nextCursor while remaining incomplete. Narrow the search or use countsOnly/count_logs for complete totals; maxQueryHits can lower the default 10,000-hit allowance across all continuations. Restart the client after upgrading to refresh search v6/count v3 schemas.

For detailed behavior and limits, see the [MCP Log Server Guide](./McpLogServerGuide.md). For the trust boundary and residual risks, see the [MCP Security and Resilience Model](./McpSecurityModel.md).
