# LogReader

LogReader is a Windows desktop tool for reading, filtering, searching, and tailing log files.

The main product lives in `LogReader/`. The peer `LogGenerator/` folder contains an internal developer utility for generating synthetic logs while working on the app.

## Documentation

- [User Guide](./LogReader/docs/UserGuide.md) - Day-to-day app usage, workflows, and shortcuts.
- [Developer Guide](./LogReader/docs/DeveloperGuide.md) - Architecture, build/test workflow, and local publish steps.

## Repository Layout

- `LogReader/` - Main application, tests, and product documentation.
- `LogGenerator/` - Internal utility for generating sample logs. See [LogGenerator README](./LogGenerator/README.md).

If you want to build and run the app from source, begin with the developer guide.
