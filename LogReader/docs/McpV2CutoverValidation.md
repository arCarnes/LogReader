# MCP 2.0 cutover validation

Source version: 2.0.0. Public MCP envelope: 5. Branch: feature/mcp-v2-contracts.

## Completed checks

- Release solution build: succeeded with zero warnings/errors.
- `dotnet test LogReader/LogReader.sln -c Release --no-build --no-restore -m:1 --filter "Category!=InteractiveDesktop"`: 1,510 WPF/service-safe tests and 1,064 Core tests passed; zero skipped in this selected lane. InteractiveDesktop tests were explicitly excluded and were not run.
- The final public-contract subset passed 38 cases with JsonSchema.Net 7.3.4. The package and its new transitive dependencies declare MIT licenses; notices are embedded in the MCP executable.
- Cursor-only replay/concurrency/authorization/expiry tests and the repaired WPF refresh-synchronization test passed three repeated focused runs. The WPF test now executes setup, assertions and cleanup inside WpfTestHost and drains each dashboard refresh explicitly.
- The standalone UNC measurement harness built successfully. No real UNC measurement was performed for this change.
- Published a local win-x64 self-contained single-file MCP executable, verified file version 2.0.0.0, and passed Test-McpStdioArtifact.ps1 against its isolated portable fixture.
- Measure-McpLogServer.ps1 passed with two local files, 1,000 lines/file, statistics and monitor consistency enabled. Schema validation, protocol/text parity, replay, fairness/backlog, frozen-extent completion after append, and explicit handoff were verified.
- A separate one-file run validated the 1,000-bucket sparse grid scenario.
- PowerShell syntax checks and git diff whitespace checks passed.

The initially restricted solution-test run could not connect to its local testhost. The completed full suite used normal local process/loopback access; timeouts and assertions were not relaxed. A pre-existing WPF synchronization race exposed by the full suite was fixed in a separate local commit.

## Pending real-client acceptance

- Claude Code 2.1.201 is installed but returned Not logged in. The user explicitly requested leaving Claude acceptance pending.
- Codex CLI 0.142.5 reached its service, but the ChatGPT account rejected both the CLI default gpt-5.5 and configured gpt-6.1-sol model selections. No WeezTail tool calls executed in either attempt. A working model/account is needed to complete acceptance.
- Neither client failure establishes a schema conversion result. Actual model-facing schema preservation and successful fresh-agent workflows remain unverified.

The synthetic acceptance fixture appends nonmatching activity continuously. The pending scenario asks each client to discover IDs, count needle, retrieve both matching lines with one hit per page, read the last two physical lines, and explicitly start/poll/stop one observation. Acceptance requires cursor-only continuation, exactly one initial search, and no filesystem/source inspection. This should be rerun with authenticated supported clients before publishing the cutover.

No branch, commit, tag or release was pushed or published. Local source versioning is not release approval.
