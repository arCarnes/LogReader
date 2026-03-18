# LogReader

LogReader is a Windows desktop tool for reading, filtering, searching, and tailing log files.

The main product lives in `LogReader/`. The peer `LogGenerator/` folder contains an internal developer utility for generating synthetic logs while working on the app.

## Documentation

- [User Guide](./LogReader/docs/UserGuide.md) - Day-to-day app usage, workflows, and shortcuts.
- [Installation Guide](./LogReader/docs/InstallationGuide.md) - Install, update, uninstall, and troubleshoot a packaged build.
- [Developer Guide](./LogReader/docs/DeveloperGuide.md) - Architecture, build/test workflow, and MSI/portable packaging.

## Repository Layout

- `LogReader/` - Main application, tests, packaging sources, and product documentation.
- `LogGenerator/` - Internal utility for generating sample logs. See [LogGenerator README](./LogGenerator/README.md).

If you received a packaged build and only need to get started, begin with the installation guide.
