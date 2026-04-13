# LogReader

LogReader is a Windows desktop tool for reading, filtering, searching, and tailing log files.

The main product lives in `LogReader/`, which is also the solution and packaging root for the app. The peer `LogGenerator/` folder contains an internal developer utility for generating synthetic logs while working on the app.

## Start Here

- [Installation Guide](./LogReader/docs/InstallationGuide.md) - Windows install options, storage layout, and packaged-app defaults.
- [User Guide](./LogReader/docs/UserGuide.md) - Day-to-day app usage, dashboards, search, filtering, and shortcuts.
- [Developer Guide](./LogReader/docs/DeveloperGuide.md) - Architecture, validation workflow, and publish steps for contributors.

## Repository Layout

- `LogReader/` - Main application solution, tests, packaging assets, and product documentation.
- `LogGenerator/` - Internal utility for generating sample logs. See [LogGenerator README](./LogGenerator/README.md).

## Test Layout

- `LogReader/LogReader.Core.Tests/` owns the non-WPF xUnit suite for core and infrastructure behavior.
- `LogReader/LogReader.Tests/` owns the WPF and app-shell xUnit suite.
- `LogReader/LogReader.Testing/` holds shared WPF-free fakes and test utilities used by the test suites.

If you want to work on the app from source, use the developer guide. If you want to install or use a packaged build, start with the installation guide and user guide.
