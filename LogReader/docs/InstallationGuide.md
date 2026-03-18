# LogReader Installation Guide

Last updated: 2026-03-18

This guide is for installing a packaged LogReader build on Windows. For day-to-day usage, see the [User Guide](./UserGuide.md). For building or packaging release artifacts, see the [Developer Guide](./DeveloperGuide.md).

## Supported Platform

- Windows
- The current MSI and portable artifacts are self-contained `win-x64` builds.

## What You Receive

You will typically receive one of these package types:

- `LogReader-<version>-win-x64.msi`
- `LogReader-<version>-win-x64-portable.zip`

## Install From MSI

1. Close any running LogReader window.
2. Run the `.msi` package.
3. Leave the install location at its default.
4. Choose the data storage folder.
5. Finish the install.

Default MSI behavior:

- Installs app files to `%LOCALAPPDATA%\Programs\LogReader`
- Creates a `LogReader` Start menu shortcut
- Registers a current-user uninstall entry in Windows
- Defaults the data storage folder to `%LOCALAPPDATA%\LogReader`

MSI uninstall removes the installed app files and shortcut but preserves your data folder.

## Use The Portable ZIP

1. Extract the ZIP to a writable folder.
2. Open the extracted folder.
3. Run `LogReader.App.exe`.

Portable behavior:

- Stores data under `.\LogReaderData` inside the extracted app folder
- Does not create a Start menu shortcut
- Does not register an uninstall entry in Windows
- Is removed by deleting the extracted folder

If you delete the portable folder, you also delete the portable app data stored inside it.

## Update or Reinstall

- Install a newer MSI by running the newer `.msi`.
- MSI upgrades replace the app files in `%LOCALAPPDATA%\Programs\LogReader` and preserve the chosen storage root and existing data.
- Update a portable build by replacing the extracted app files with the contents of the newer ZIP.
- When moving between MSI and portable package types, copy your saved data manually.

## Uninstall

MSI builds can be uninstalled from:

- Using the `LogReader` entry in Windows Installed apps / Apps & features

Portable builds are removed by deleting the extracted app folder.

## Move Existing Data

Package types do not migrate data automatically.

To move from MSI to portable:

1. Close LogReader.
2. Copy the contents of `%LOCALAPPDATA%\LogReader` into the portable `LogReaderData` folder.

To move from portable to MSI:

1. Close LogReader.
2. Copy the contents of the portable `LogReaderData` folder into the MSI storage folder you selected during install.

## Troubleshooting

- MSI install fails or repair prompts for a new path: rerun the `.msi` and keep the install location at `%LOCALAPPDATA%\Programs\LogReader`.
- LogReader starts but cannot save data: confirm the selected MSI storage folder or the portable extraction folder is writable.
- Portable data seems missing after moving builds: check whether your files are still in `%LOCALAPPDATA%\LogReader` or in the portable `LogReaderData` folder.
