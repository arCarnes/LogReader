# Installation Guide

Last updated: 2026-10-12

WeezTail offers four Windows x64 packages: a fully portable ZIP, an AppData ZIP for local or shared-drive use, a shared-release ZIP with stable launchers, and an MSI installer. A separate launcher-only ZIP is available for maintaining shared deployments.

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

## Shared-release ZIP and stable launchers

Use `WeezTail-<version>-shared-win-x64.zip` when several users run WeezTail from a network share. Extract it into a new deployment root:

```text
\\server\tools\WeezTail\
  WeezTail.Launcher.exe
  WeezTail.Mcp.Launcher.exe
  current.json
  releases\
    <version>\
      WeezTail.exe
      WeezTail.Mcp.exe
      WeezTail.install.json
```

Create desktop shortcuts to `WeezTail.Launcher.exe`. Configure MCP clients with the absolute UNC path to `WeezTail.Mcp.Launcher.exe`, without arguments. The desktop launcher forwards arguments and exits after starting the app. The MCP launcher stays running, relays the server's stdio, and stops its server process when the client closes or terminates the launcher.

In a shared-release deployment, the **MCP Server** menu shows and copies the stable
`WeezTail.Mcp.Launcher.exe` path at the deployment root. Use that path for MCP clients
so release promotion or rollback does not require editing client configuration.

On each shared-deployment launch, WeezTail creates or refreshes a per-user Start
menu shortcut named **WeezTail** under **WeezTail Shared\<deployment hash>**. The
shortcut and running window use the same taskbar identity. Only the shortcut is
stored in your user profile; all executables remain on the share. WeezTail does
not pin anything automatically or modify your existing desktop shortcuts.

The updated application replaces its old generated **WeezTail (<deployment hash>)**
Start menu shortcut after successfully creating the friendly shortcut. It removes
only the exact legacy link with matching deployment metadata; modified, unrelated,
or unreadable links are preserved. If migration fails, startup continues with
window relaunch properties and retries on a later refresh. Older application
versions can recreate the legacy shortcut when run again.

Existing taskbar pins are not renamed automatically. To remove the hash from an
existing pin, close WeezTail, unpin the old entry, start the updated application
through `WeezTail.Launcher.exe`, and pin the running application again.

The generated **WeezTail Shared** Start menu shortcut follows the application's
holiday icon calendar. It refreshes on startup, activation, and every minute while
WeezTail runs, then restores the launcher's standard icon after the holiday. If
WeezTail is closed at the date boundary, the shortcut catches up on the next launch;
no second release is needed. Seasonal ICO files are cached per user and deployment
under `%LocalAppData%\WeezTail\ShellIcons`, outside versioned release folders. If the
cache cannot be prepared, the shortcut uses the standard launcher icon and retries
on later refreshes. Its name, launcher target, arguments, and taskbar identity stay
the same. Existing taskbar pins are not edited, and their visible icons may retain
the previous appearance. Installer-created and user-created shortcuts are unchanged.

To pin the deployment, start `WeezTail.Launcher.exe`, right-click the running
WeezTail taskbar icon, and select **Pin to taskbar**. Alternatively, locate the
generated shortcut in Start and select **Pin to taskbar** (under **More** on some
Windows versions). The pin targets the stable launcher, which reads `current.json`
on each launch, so it follows promotion and rollback. Original launch arguments
are not saved in the shortcut or pin.

The updated application recognizes the shared-release layout even when launched
directly from `releases\<version>\WeezTail.exe` or through an older launcher. It
requires the root `current.json`, `WeezTail.Launcher.exe`, and the release's
Portable/LocalAppData installation configuration. If Start menu registration
fails, startup continues with window relaunch properties as a fallback.

Unpin older entries and recreate them through the updated application or generated
shortcut. Use one consistent deployment path: UNC and mapped-drive spellings have
separate identities, as do moved deployment roots. Shortcuts for moved roots are not removed
automatically; remove obsolete entries yourself after moving a deployment.

Windows may still reject a taskbar pin whose target is a network executable.
Shortcut identity and window-property checks do not establish that a particular
Windows installation accepts that target. Live-share pinning remains a manual
deployment acceptance check:

1. Launch from the share and confirm the generated Start menu shortcut appears.
2. Test pinning both the running window and the shortcut; distinguish a missing
   **Pin to taskbar** option from an action that has no effect.
3. Close WeezTail and reopen each successful pin. Confirm the running window groups
   with that pin rather than appearing as a second taskbar entry.
4. Promote and roll back `current.json`; confirm the same pin opens the selected
   release after closing the previous instance.
5. Start the updated versioned application directly and confirm the shortcut and
   taskbar identity still work without a launcher handoff.
6. Repeat independently for UNC and mapped-drive paths, removing old pins between
   cases. A mapped drive must be available in the launching account and session.

`current.json` selects the approved version, for example:

```json
{"version":"1.5.9"}
```

Each launch reads this pointer once and runs directly from `releases/<version>`. The version must have three numeric fields. The launchers require AppData storage and reject redirected release directories or files. Each Windows user's data and cache remain under `%LOCALAPPDATA%\WeezTail`; no state is written beside the launchers. The share must be available at launch, and the launchers do not download, cache, or silently select another version.

### Promote a release

1. Build and validate the new release, then extract its **AppData ZIP** into a new `releases/<version>` directory. Do not replace or modify a published release directory.
2. Pilot the version by launching its desktop executable directly and configuring a test MCP client with its sidecar path.
3. Replace `current.json` atomically using a temporary file in the same deployment root. The following PowerShell example selects an already copied and verified release:

```powershell
$deploymentRoot = '\\server\tools\WeezTail'
$releaseVersion = '1.5.9'
$pointerPath = Join-Path $deploymentRoot 'current.json'
$temporaryPointer = Join-Path $deploymentRoot ('current.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$pointerJson = @{ version = $releaseVersion } | ConvertTo-Json
[System.IO.File]::WriteAllText($temporaryPointer, $pointerJson)
[System.IO.File]::Replace($temporaryPointer, $pointerPath, $null)
```

The pointer already exists after initial extraction. If replacement fails, keep the existing pointer, investigate the share's permissions or replacement support, and remove the temporary file after checking it. Do not delete the live pointer first.

Existing desktop and MCP sessions continue using their selected release. Users get the promoted release after closing and reopening the desktop app or restarting the client's MCP server. The desktop app still permits only one instance per Windows session.

**Never extract a complete shared ZIP over a live deployment.** Its pointer would promote the release before copying finishes. Use the AppData ZIP for new release folders. Keep old folders until their processes have closed.

To update launcher binaries, use `WeezTail-<version>-launchers-win-x64.zip`
from the application release. Both launchers are built with that application's
version. Existing shared deployments can retain older launchers when promoting
an application release; replace them when a release includes launcher fixes.
The application version selected by `current.json` can differ from the installed
launcher version.

The maintenance ZIP includes `current.example.json`, which contains an example
application version and must not replace the live pointer. Close all MCP launcher
instances before replacing its executable, and wait for desktop launcher invocations
to finish.

### Rollback and permissions

Rollback selects a retained release with the same atomic pointer replacement. It does not restore user data: confirm that the older release can read the settings and dashboard formats written by the newer version, and back up user data before releases that change those formats.

Users require read/execute access to the deployment and access to their logs. Only maintainers should have write access to the launchers, pointer, and release directories: those files determine which executable runs under each user's account.

Desktop launch errors appear in a dialog. MCP launcher diagnostics appear only on stderr; stdout is reserved for the MCP protocol. Both return `1` for launch failures; MCP rejects arguments with `2` and otherwise returns the server's exit code. A server that fails to stop within five seconds after stdin closes is terminated and reported as a failure.

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
