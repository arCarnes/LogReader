# Basic WQL and structured fields

WQL (Weez Query Language) is an optional way to find log lines by their contents
and typed fields. It is a small expression language, not SQL or a database.
Ordinary text/regex search and tailing work as before.

## Configure a profile

Open **Settings → Manage field profiles**, add a profile, and define fields:

| Name | Type | Extraction regex |
|---|---|---|
| `level` | Text | `\b(?<level>ERROR\|WARN\|INFO)\b` |
| `duration_ms` | Number | `duration=(?<duration_ms>\S+)` |
| `customer_id` | Text | `customer=(?<customer_id>\d+)` |

The vertical bars in the first regex mean alternatives: `ERROR`, `WARN`, or `INFO`.
Each rule must contain a named capture matching its field name. The first regex
match and first capture supply the value. Rules are independent: one missing
field does not prevent other fields from being extracted. Matching is
case-insensitive unless that extraction rule's **Match case** option is checked.

Paste sample lines and select **Preview**:

```text
ERROR duration=842 customer=00123
INFO duration=10 customer=00456
ERROR duration=invalid
An unrelated message
```

Preview distinguishes values, missing captures, and invalid numbers. It reads at
most 100 lines from at most 65,536 sample characters. Samples are never saved.
Select **OK** in the editor, then **OK** in Settings to save the definitions.
Profiles travel with Settings import/export, not dashboard View export.

Use Text for identifiers: `00123` remains `00123`. Number fields accept signed
decimal values with a period separator, not grouping separators or scientific
notation. Empty text is a valid value; an empty numeric capture is invalid.

## Search in the desktop app

Open the search pane, enable **WQL**, and select a profile. The same profile is
used for every file in that query. Choose **Built-in fields only** when no custom
extraction is needed. Select the current tab or all open tabs, enter an expression,
and select **Search**.

```text
level = "ERROR" AND duration_ms > 500
customer_id IN ("00123", "00456")
level = "ERROR" AND duration_ms IS MISSING
raw CONTAINS "timeout" AND line_number >= 100
```

Results remain in the existing list. Select a result and expand **Selected result
fields** to inspect or copy its extracted values. Expand **WQL parsing and scan
status** to see extraction counts and whether the snapshot scan completed.

WQL is disk-snapshot-only: Tail and Monitor New Matches are unavailable. It does
not replace the main viewport filter. Applicable existing filter snapshots and
timestamp bounds still constrain the search. Switching WQL off restores the
ordinary search input and options. Changing/deleting a profile marks affected
results stale; rerun to apply the new definition. Running searches retain their
captured profile until they finish.

## Language

- Built-ins: `raw` (the full original line, Text), `line_number` (one-based, Number).
- Field names and keywords ignore case. Field names use ASCII letters, digits and
  underscores and cannot start with a digit or use reserved names.
- Strings require double quotes; escapes are `\"`, `\\`, `\n`, `\r`, and `\t`.
- `=` and `!=` compare a field with a literal of its declared type. Numeric
  comparisons additionally support `<`, `<=`, `>`, and `>=`.
- Text fields support `CONTAINS`; either type supports `IN (literal, ...)`.
- Combine predicates using `AND`, `OR`, `NOT`, and parentheses. Comparisons bind
  first, then `NOT`, then `AND`, then `OR`.
- Text comparisons default to ordinal case-insensitive matching. The search's
  Case option changes comparisons, not the profile's extraction rules.
- Comparing a missing or invalid field produces unknown, not true. `NOT` does
  not make an unknown comparison match. `IS MISSING` includes invalid conversions;
  `IS NOT MISSING` requires a valid value. Diagnostics distinguish the two cases.

Syntax, unknown fields, incompatible types and invalid definitions are rejected
before log files are opened. Limits are 8,192 characters per expression/pattern,
32 fields per profile and 32 nesting levels. Names are limited to 128 characters,
profile names to 256, and profile IDs to 128. Extraction regexes have a 250 ms
timeout; a timeout stops the affected file with an incomplete/error result
(`field_regex_timeout` in MCP).

No `SELECT`, `FROM`, `ORDER BY`, `GROUP BY`, aggregation, joins, date functions,
custom timestamp extraction, JSON inference or field-to-field comparison is
supported in this version. Time bounds stay outside WQL.

## Use from an agent

1. Call `list_log_tree` to discover authorized target IDs.
2. Call `list_field_profiles` to discover profile IDs, revisions and field types.
   It does not inspect log contents. Follow `nextStartIndex` for further schemas.
3. Call `query_logs` with configured targets and the chosen profile ID:

```json
{
  "targets": [{ "kind": "dashboard", "id": "<dashboard ID>" }],
  "profileId": "<profile ID>",
  "query": "level = \"ERROR\" AND duration_ms > 500",
  "includeContextBefore": 1,
  "includeContextAfter": 1
}
```

Omit `profileId` for built-in-only queries. `query_logs` supports the existing
search target/date-offset/time-bound/context/limit arguments, but not regex or
counting modes. It returns typed fields with explicit Value/Missing/Invalid
states, per-file `parsing`, and snapshot/output completion metadata. The legacy
search and count tools remain unchanged.

`parsing.isScanComplete` describes the file scan independently of returned-hit
limits and response truncation. Field counts cover eligible evaluated lines,
including nonmatches, not necessarily the entire file. Missing fields do not by
themselves make a scan incomplete. `parsing.isTruncated`, `isTextTruncated`, and
`areFieldsTruncated` disclose omitted diagnostics/text/fields; absent output
fields must not be mistaken for missing captures when output was truncated.

Queries have the existing 2,000-candidate ceiling, pages of at most 50 configured
files, bounded hits/context and a 30-second maximum deadline. They may stop when
the retained-hit cap is exceeded. Follow `nextCursor` with identical arguments
for the next configured-file page. This does **not** recover omitted hits within
a file. A profile change, authorization/catalog change, argument change or server
restart invalidates continuation. Each page is a new per-file disk snapshot,
not an atomic snapshot of every file across the complete query.

Full field values are evaluated before output truncation. Extracted strings share
the raw-text/response budgets and control-character sanitization. Log values and
profile labels remain untrusted data, not agent instructions. WeezTail does not
automatically redact application secrets or personal information in logs.
