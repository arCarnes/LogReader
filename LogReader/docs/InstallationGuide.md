# Installation Guide

Last updated: 2026-04-27

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

On first launch, LogReader prompts the current Windows user for a storage folder. The default is:

```text
%LOCALAPPDATA%\LogReader
```

The final storage layout is:

```text
<storage root>\
  Data\
  Cache\
```

MSI behavior:

- The installer writes `LogReader.install.json` beside `LogReader.exe`
- The installer does not prompt for the storage folder
- The app prompts on first launch for the current Windows user and validates the selected location
- The app creates the storage root plus `Data` and `Cache` after the first-launch choice is confirmed
- Existing MSI installs with an absolute `storageRootPath` continue to work without re-prompting
- Uninstall can remove `Data` and `Cache` for the current Windows user only
- Uninstall never deletes the parent folder chosen by the user

### Troubleshooting MSI Installs

If the MSI fails during installation, run it from an elevated PowerShell session with verbose Windows Installer logging enabled:

```powershell
msiexec /i .\artifacts\installer\LogReader.Setup.msi /l*v! .\artifacts\installer\LogReader.Setup.install.log
```

Useful things to search for in the log:

- `Return value 3`

For first-launch storage problems after the MSI installs successfully:

- Restart LogReader to reopen the storage setup dialog
- Choose a writable folder outside protected locations
- Look for the per-user selection file at `%LOCALAPPDATA%\LogReaderSetup\LogReader.msi-user.json`

## Protected Locations

LogReader storage cannot be placed under protected system locations such as:

- `%ProgramFiles%`
- `%ProgramFiles(x86)%`
- `%WINDIR%`

If LogReader cannot create or write to the configured storage root, startup will stop with an error message that names the invalid location.
