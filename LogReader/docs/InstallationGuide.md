# Installation Guide

LogReader supports two install modes on Windows x64: `Portable` and `MSI`.

## Portable

Portable packages use this layout:

```text
Portable\
  LogReader.exe
  LogReader.install.json
  Data\
  Cache\
```

Portable storage rules:

- `Data` and `Cache` live beside `LogReader.exe`
- Moving the portable folder moves the app state with it
- LogReader validates the portable location at startup
- Portable installs fail to start from protected locations such as `Program Files` or the Windows directory

## MSI

The MSI is a per-machine installer.

Installer prompts:

- Install directory, default: `%ProgramFiles%\LogReader`
- Data folder parent, default: `%LOCALAPPDATA%`

The installer appends `\LogReader` to the selected data folder parent, so the final storage root is:

```text
<chosen data folder parent>\LogReader
```

The final storage layout is:

```text
<storage root>\
  Data\
  Cache\
```

MSI behavior:

- The installer writes `LogReader.install.json` beside `LogReader.exe`
- The installer creates the storage root plus `Data` and `Cache`
- The installer fails with a clear error if the chosen data location is protected or not writable
- Uninstall prompts whether to remove `Data` and `Cache`
- Uninstall never deletes the parent folder originally selected by the user

## Protected Locations

LogReader storage cannot be placed under protected system locations such as:

- `%ProgramFiles%`
- `%ProgramFiles(x86)%`
- `%WINDIR%`

If LogReader cannot create or write to the configured storage root, startup or installation will stop with an error message that names the invalid location.
