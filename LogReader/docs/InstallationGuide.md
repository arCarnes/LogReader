# LogReader Installation Guide

Last updated: 2026-03-18

This guide is for installing a staged LogReader build on Windows. For day-to-day usage, see the [User Guide](./UserGuide.md). For building or packaging the installer, see the [Developer Guide](./DeveloperGuide.md).

## Supported Platform

- Windows
- The default staged installer is framework-dependent and requires the .NET 8 Desktop Runtime.
- If you received a self-contained build from your distributor, the runtime prerequisite does not apply.

## What You Receive

A standard staged installer folder contains:

- `Setup.cmd`
- `Install-LogReader.ps1`
- `Uninstall-LogReader.ps1`
- `installer-manifest.json`
- `payload\`

Install LogReader from that folder without moving files out of it.

## Install LogReader

1. Close any running LogReader window.
2. Open the staged installer folder.
3. Run `Setup.cmd`.
4. Wait for setup to finish.

By default, setup:

- Installs LogReader to `%LOCALAPPDATA%\Programs\LogReader`
- Creates a `LogReader` Start menu shortcut
- Registers a current-user uninstall entry in Windows
- Launches LogReader when installation finishes

LogReader stores its app data under `%LOCALAPPDATA%\LogReader`.

## Update or Reinstall

- Install a newer build by running `Setup.cmd` from the newer staged folder.
- Setup stops the currently installed LogReader process, replaces the files in the install directory, and keeps your app data.
- Reinstalling the same build refreshes the installed files in place.

## Uninstall

You can uninstall LogReader by either:

- Using the `LogReader` entry in Windows Installed apps / Apps & features
- Running `%LOCALAPPDATA%\Programs\LogReader\Uninstall-LogReader.ps1`

Default uninstall behavior:

- Prompts for confirmation
- Removes the installed app files
- Removes the Start menu shortcut
- Removes the current-user uninstall entry
- Preserves `%LOCALAPPDATA%\LogReader` app data

To remove the app data too, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\LogReader\Uninstall-LogReader.ps1" -RemoveData
```

If your install location was customized, run `Uninstall-LogReader.ps1 -RemoveData` from that install folder instead.

## Troubleshooting

- Setup exits immediately or reports missing files: make sure `Setup.cmd`, the PowerShell scripts, `installer-manifest.json`, and the `payload` folder are still together.
- LogReader installs but does not start: install the .NET 8 Desktop Runtime or ask for a self-contained build.
- The Start menu shortcut or uninstall entry is missing: rerun `Setup.cmd` from a complete staged folder.
- Reinstall fails because files are in use: close LogReader and rerun setup.
