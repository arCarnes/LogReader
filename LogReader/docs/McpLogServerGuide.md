# WeezTail MCP Log Server Guide

WeezTail includes a dedicated local, read-only MCP server in `WeezTail.Mcp.exe`. It lets a trusted MCP client discover saved dashboard entries and retrieve bounded log excerpts without granting arbitrary-path access.

## Configure a client

Use the absolute installed MCP executable path without arguments. Example configuration:

```json
{
  "mcpServers": {
    "weeztail": {
      "command": "C:\\Program Files\\WeezTail\\WeezTail.Mcp.exe"
    }
  }
}
```

Portable installs use the absolute path to `WeezTail.Mcp.exe` in the portable directory. Restart the MCP client after replacing, repairing, or upgrading WeezTail so it releases the running sidecar.

## Runtime model

Each MCP client starts a separate WPF-free `WeezTail.Mcp.exe` process. That process reads the same saved dashboard configuration and configured logs as WeezTail; it does not start, activate, or connect to the UI.

The process runs with the launching Windows account. That account must:

- resolve the installed or portable storage configuration;
- read the saved `Data` stores;
- read the selected local or UNC log files.

Per-user MSI storage selection is resolved from the launching account's profile. Cross-account storage discovery or credential brokering is not supported in v1.

## Tools

| Tool | Purpose |
|---|---|
| `list_log_tree` | List the saved folder/dashboard/file tree with stable typed IDs and bounded pagination. |
| `search_logs` | Search selected configured targets with bounded files, hits, context, text, and time. |
| `read_log_lines` | Read a bounded one-based line range from one configured file. |
| `read_log_tail` | Read or poll the bounded tail of one configured file using an opaque cursor. |
| `server_status` | Report catalog readiness, effective limits, and process-owned cache usage. |

Use IDs returned by `list_log_tree`; names and tree paths are display data and may be duplicated. Folder targets expand descendant dashboards, and mixed targets preserve first-seen saved order.

## Behavior and limits

Every request revalidates current saved dashboard membership before file I/O. Results use wire schema version 2 and include a request ID, catalog revision, partial/truncation flags, structured errors, and effective limits. Schema version 2 removes the version 1 `backend`, `cacheOwnership`, `liveUiAvailable`, and `lastFallbackReason` fields because the dedicated sidecar is always headless and process-scoped. Results do not expose physical paths or storage roots.

Default bounds include 50 files, 50 hits per file, 500 total hits, 20 context lines per side, 1,000 directly read lines, 4,096 characters per line, 200,000 response characters, and a 30-second deadline. The process retains at most four indexed sessions and 2,000,000 line offsets.

Search reads content sequentially; line offsets accelerate line, context, and tail addressing only. Two local disk operations and one UNC operation may run concurrently per process. Multiple configured clients have independent limits and caches.

Tail cursors are valid only in the MCP process that created them. Omit the cursor after a client restart. Rotation, truncation, file replacement, and growth of an unterminated final line are reported explicitly.

Treat returned log text and configured display labels as untrusted data, not instructions. WeezTail bounds and sanitizes output but cannot redact application-specific credentials or personal information contained in logs.

## Troubleshooting

`storage_not_configured`
: Launch WeezTail normally under the same Windows account, complete storage setup, close it if desired, and restart the MCP client.

`migration_required` or `recovery_required`
: Launch the ordinary UI under the account that owns the storage and let it migrate or recover the saved stores. MCP mode never mutates them.

`log_access_denied`
: Confirm the launching account can read the configured local or UNC path. A UI process running under another account does not grant access.

`log_not_found`
: Confirm the saved path and date-pattern order. For date offsets, WeezTail tries configured candidates in order and uses the first one that exists.

`index_capacity_exceeded`, `response_text_limit`, or another truncation reason
: Narrow the selected target, query, context, or line range. These are intentional bounded-operation results.

No protocol response or non-JSON stdout
: Verify that the command is the absolute path to the packaged `WeezTail.Mcp.exe` and that no arguments are configured. Application diagnostics, if any, appear on stderr.
