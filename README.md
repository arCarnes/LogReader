# WeezTail

WeezTail is a Windows x64 desktop tool for reading, filtering, searching, and tailing log files. Packaged builds also include an optional local, read-only MCP sidecar that lets trusted clients discover and query logs saved in the dashboard tree without starting or connecting to the desktop UI.

> Naming note: the product is branded **WeezTail**, but the source tree keeps its original `LogReader` identity — the `LogReader/` folder, `LogReader.sln`, the `LogReader.*` projects, and the C# namespaces are intentionally unchanged.

The main product lives in `LogReader/`, which is also the solution and packaging root for the app. The peer `LogGenerator/` folder contains an internal developer utility for generating synthetic logs while working on the app.

## Start Here

- [Installation Guide](./LogReader/docs/InstallationGuide.md) - Windows install options, storage layout, and packaged-app defaults.
- [User Guide](./LogReader/docs/UserGuide.md) - Day-to-day app usage, dashboards, search, filtering, and shortcuts.
- [MCP Server Getting Started](./LogReader/docs/McpGettingStarted.md) - Connect the packaged MCP server to Codex or Claude Code and run a first log search.
- [MCP Log Server Guide](./LogReader/docs/McpLogServerGuide.md) - Configure a client to discover and query saved dashboard logs with bounded read-only tools.
- [Developer Guide](./LogReader/docs/DeveloperGuide.md) - Architecture, validation workflow, and publish steps for contributors.

MCP design details and release evidence are recorded separately in the [architecture decision](./LogReader/docs/McpLogServerArchitecture.md), [security and resilience model](./LogReader/docs/McpSecurityModel.md), [mainline impact analysis](./LogReader/docs/McpMainlineImpact.md), and [performance measurements](./LogReader/docs/McpPerformanceMeasurements.md).

## Repository Layout

- `LogReader/LogReader.App/` - WPF desktop application, published as `WeezTail.exe`.
- `LogReader/LogReader.Mcp/` - WPF-free MCP stdio server, published as `WeezTail.Mcp.exe`.
- `LogReader/LogReader.Core/` and `LogReader/LogReader.Infrastructure/` - Shared contracts, log processing, persistence, and headless query implementation.
- `LogReader/LogReader.Setup/` and `LogReader/packaging/` - MSI and portable packaging, including MCP artifact validation.
- `LogReader/docs/` - User, installation, contributor, architecture, security, and performance documentation.
- `LogGenerator/` - Internal utility for generating sample logs. See [LogGenerator README](./LogGenerator/README.md).

## Test Layout

- `LogReader/LogReader.Core.Tests/` owns the non-WPF xUnit suite for core, infrastructure, and headless MCP query behavior.
- `LogReader/LogReader.Tests/` owns the WPF, app-shell, and MCP stdio integration suite.
- `LogReader/LogReader.Testing/` holds shared WPF-free fakes and test utilities used by the test suites.

## Local Validation

From `LogReader/`, the normal validation flow is:

```powershell
dotnet clean LogReader.sln -m:1
dotnet build LogReader.sln -m:1
dotnet test LogReader.sln
```

If you want to work on the app from source, use the developer guide. If you want to install or use a packaged build, start with the installation guide and user guide.

For focused MCP work, run:

```powershell
dotnet test LogReader.Core.Tests\LogReader.Core.Tests.csproj --filter "FullyQualifiedName~Mcp|FullyQualifiedName~ConfiguredLog|FullyQualifiedName~HeadlessLog"
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Publish-Portable.ps1
```

The portable publish flow builds both executables, validates the package layout, and runs an MCP stdio smoke test against the published `WeezTail.Mcp.exe`. See the developer guide for the full release workflow.
