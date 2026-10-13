# Installation Guide

Last updated: 2026-10-12

WeezTail offers three Windows x64 packages: a fully portable ZIP, an AppData ZIP for local or shared-drive use, and an MSI installer.

All packages include the optional read-only MCP log server as the dedicated `WeezTail.Mcp.exe` sidecar. It adds no service, daemon, port, or installer feature and reads the same saved configuration as the desktop app. See the [MCP Log Server Guide](./McpLogServerGuide.md) after installation.

## Portable

Portable packages use this layout:

```text
Portable\
  WeezTail.exe
  WeezTail.Mcp.exe
  WeezTail.install.json
  Data\
  Cache\
```

Portable storage rules:

- `Data` lives beside `WeezTail.exe`
- Runtime cache lives in `Cache` beside `WeezTail.exe`, including when using `WeezTail.Mcp.exe`
- Moving the portable folder moves the app state with it; configured log paths must still be accessible on the destination machine
- The folder must be writable; storage failures do not switch to AppData
- Upgrading an existing portable folder keeps its `Data` in place and rebuilds cache beside the executable. Earlier AppData caches are left untouched because an MSI installation may still use them
- WeezTail validates the portable location at startup
- Portable installs fail to start from protected locations such as `Program Files` or the Windows directory

## AppData ZIP and shared drives

The AppData ZIP needs no installation. Extract it to a local folder or a shared folder:

```text
AppData\
  WeezTail.exe
  WeezTail.Mcp.exe
  WeezTail.install.json
```

Both executables automatically use the executing Windows user's storage:

```text
%LOCALAPPDATA%\WeezTail\
  Data\
  Cache\
```

- No storage setup prompt is shown, and no application data or cache is written beside the executables.
- Multiple users on different machines can run the same shared executables. Each MCP client starts its own local stdio process; no shared service or port is created.
- Run the desktop app and MCP client under the same Windows account to read the same saved dashboards and settings.
- Existing data at the standard AppData location is reused. Custom MSI folder selections are ignored by this ZIP.
- Users need read/execute access to the package and access to their configured logs. Restrict package modification to maintainers.
- Configure MCP clients with the absolute UNC path to `WeezTail.Mcp.exe`; mapped drives must be available to the launching account and session.
- Before replacing the shared package, close desktop instances and MCP clients on all machines because running processes may hold executables open. Versioned deployment folders can avoid replacing running files; update shortcuts and MCP paths accordingly.
- Copying this ZIP folder does not carry saved configuration. Use WeezTail's import/export features to transfer data between package modes.
- Unavailable local storage produces an error; it does not switch to the shared folder.

Both ZIPs are self-contained single-file builds. The .NET runtime can extract native files under local `%TEMP%`; the portable guarantee covers persistent WeezTail data and cache.

## MSI

The MSI is a per-machine installer.

Installer prompts:

- Install directory, default: `%ProgramFiles%\WeezTail`
- Start menu shortcut feature, installed by default for the installing Windows user
- Desktop shortcut feature, optional and installed for the installing Windows user when selected

On first launch, WeezTail prompts the current Windows user for a storage folder. The default is:

```text
%LOCALAPPDATA%\WeezTail
```

The final storage layout is:

```text
<storage root>\
  Data\

%LOCALAPPDATA%\WeezTail\
  Cache\
```

MSI behavior:

- The installer writes `WeezTail.install.json` beside `WeezTail.exe`
- The installer does not prompt for the storage folder
- Shortcut features are per-user MSI resources; another Windows user can run the installed executable from `%ProgramFiles%\WeezTail`, but shortcuts are created for the user who selected the features
- The app prompts on first launch for the current Windows user and validates the selected location
- The app creates `Data` inside the chosen root and creates the cache separately under `%LOCALAPPDATA%\WeezTail\Cache`
- Upgrades from LogReader adopt the existing per-user selection, legacy absolute install configuration, or `%LOCALAPPDATA%\LogReader` root; the legacy files are left in place
- Existing MSI installs with an absolute `storageRootPath` continue to work without re-prompting
- Uninstall retains user data by default. Explicit cleanup validates the selected root’s `Data` and the current user’s `%LOCALAPPDATA%\WeezTail\Cache`, stages them on their existing volumes, and deletes the staged data only after Windows Installer commits the uninstall
- If uninstall rolls back, staged data is restored. A later explicit cleanup recovers valid staging left by interruption; conflicts or unrecognized recovery metadata are preserved and logged for manual review
- Supplied cleanup paths must match the resolved application locations. Unsafe or redirected paths are retained and logged; upgrades never opt into user-data removal
- Cleanup requested from a LocalSystem context is retained because setup cannot identify an authoritative current user
- Other historical `Cache` folders beneath custom/legacy data roots are retained because their ownership cannot be established safely
- Uninstall never deletes the parent folder chosen by the user
- Active MCP clients should be closed before repair, upgrade, or uninstall because their client-owned `WeezTail.Mcp.exe` process may hold the sidecar executable open

### Troubleshooting MSI Installs

If the MSI fails during installation, run it from an elevated PowerShell session with verbose Windows Installer logging enabled:

```powershell
msiexec /i .\artifacts\installer\WeezTail.Setup.msi /l*v! .\artifacts\installer\WeezTail.Setup.install.log
```

Useful things to search for in the log:

- `Return value 3`

For first-launch storage problems after the MSI installs successfully:

- Restart WeezTail to reopen the storage setup dialog
- Choose a writable folder outside protected locations
- Look for the per-user selection file at `%LOCALAPPDATA%\WeezTailSetup\WeezTail.msi-user.json`

## Protected Locations

WeezTail storage cannot be placed under protected system locations such as:

- `%ProgramFiles%`
- `%ProgramFiles(x86)%`
- `%WINDIR%`

WeezTail also rejects broad storage roots that could already contain unrelated `Data` or `Cache` folders:

- drive roots such as `C:\`
- `%USERPROFILE%`
- `%LOCALAPPDATA%`
- `%APPDATA%`
- `%TEMP%`

Choose a WeezTail-specific child folder instead, such as `%LOCALAPPDATA%\WeezTail` or another folder whose path clearly includes `WeezTail`. The legacy `%LOCALAPPDATA%\LogReader` root remains accepted for upgraded installations.

If WeezTail cannot create or write to the configured storage root, startup will stop with an error message that names the invalid location.
