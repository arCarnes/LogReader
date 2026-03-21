# LogReader

LogReader is a Windows desktop tool for reading, filtering, searching, and tailing log files.

The main product lives in `LogReader/`. The peer `LogGenerator/` folder contains an internal developer utility for generating synthetic logs while working on the app.

## Documentation

- [User Guide](./LogReader/docs/UserGuide.md) - Day-to-day app usage, workflows, and shortcuts.
- [Installation Guide](./LogReader/docs/InstallationGuide.md) - Portable and MSI install options, defaults, and storage rules.
- [Developer Guide](./LogReader/docs/DeveloperGuide.md) - Architecture, build/test workflow, and local publish steps.

## Repository Layout

- `LogReader/` - Main application, tests, packaging assets, and product documentation. This is the product root for the app solution and release scripts.
- `LogGenerator/` - Internal utility for generating sample logs. See [LogGenerator README](./LogGenerator/README.md).

## Test Layout

- `LogReader/LogReader.Core.Tests/` owns the non-WPF xUnit suite for core and infrastructure behavior.
- `LogReader/LogReader.Tests/` owns the WPF and app-shell xUnit suite.
- `LogReader/LogReader.Testing/` holds shared WPF-free fakes and test utilities used by the test suites.

If you want to build and run the app from source, or you need the shell edit map for app changes, begin with the developer guide.
