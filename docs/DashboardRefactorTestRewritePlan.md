# Dashboard Refactor Test Rewrite (Historical Record)

Last updated: 2026-03-08

## Status

Completed.

This document is retained as historical context for the dashboard-tree refactor. The work described here has already been implemented and validated in the current test suite.

## Refactor Outcomes

The test suite now reflects these rules:

- `Branch` nodes are organizational and do not contain files.
- `Dashboard` nodes are leaf nodes and contain file memberships.
- Only one dashboard is active as a runtime tab filter.
- Tree traversal and file resolution are cycle-safe.

## Current Test Layout

Key test files after the refactor:

- `LogReader.Tests/MainViewModelTests.cs`
- `LogReader.Tests/DashboardTreeTests.cs`
- `LogReader.Tests/DashboardPersistenceTests.cs`
- `LogReader.Tests/NavigationTests.cs`
- `LogReader.Tests/LogTabViewModelLoadTests.cs`
- `LogReader.Tests/LineIndexTests.cs`
- `LogReader.Tests/LineIndexEncodingTests.cs`
- `LogReader.Tests/LineHighlighterTests.cs`
- `LogReader.Tests/JsonSettingsRepositoryTests.cs`
- `LogReader.Tests/JsonSessionRepositoryTests.cs`
- `LogReader.Tests/RotationDetectionTests.cs`
- `LogReader.Tests/SearchServiceTests.cs`
- `LogReader.Tests/SearchPanelViewModelTests.cs`

## Validation Command

```powershell
dotnet test LogReader.Tests\LogReader.Tests.csproj --framework net8.0-windows
```

## Notes for Future Changes

- If dashboard semantics change, update `DashboardTreeTests` first.
- Keep persistence behavior and malformed topology handling covered in tests.
- Avoid embedding hard-coded total test counts in docs to reduce drift.

