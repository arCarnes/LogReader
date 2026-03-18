# LogReader User Guide

Last updated: 2026-03-18

LogReader is a Windows desktop tool for reading, filtering, searching, and tailing log files. This guide assumes the app is already installed. For setup help, see the [Installation Guide](./InstallationGuide.md). For contributor workflows and architecture, see the [Developer Guide](./DeveloperGuide.md).

## Main Layout

- Left pane: dashboard tree
- Center: tabbed log viewer
- Right pane: `Search`, `Filter`, and `Go To` tools
- Bottom: status bar

Pane shortcuts:

- `Ctrl+1`: toggle dashboards pane
- `Ctrl+2`: toggle search pane
- `F10`: reader focus mode, which toggles both panes together

You can also open the built-in shortcut reference from the toolbar `Hotkeys` button or the `View` menu.

## Open Log Files

Open files by:

- Using `Open Log File...` from the toolbar or File menu (`Ctrl+O`)
- Dragging and dropping files from Explorer
- Clicking a dashboard row to open its member files

If a file is already open, LogReader activates the existing tab instead of opening a duplicate.

## Work with Tabs

Right-click a tab to:

- Pin or unpin it
- Close it
- Close other tabs
- Close all but pinned tabs
- Close all tabs

Pinned tabs are sorted before unpinned tabs in the current workspace.

Tab shortcuts:

- `Ctrl+W`: close the selected tab
- `Ctrl+Left`: select the previous visible tab
- `Ctrl+Right`: select the next visible tab

## Read and Navigate Logs

Each tab shows line numbers and log text in a monospace font.

Viewer behavior:

- Virtualized rendering for large files
- Vertical navigation with the custom scrollbar and mouse wheel
- Horizontal scrolling for long lines

Tab toolbar actions:

- Encoding dropdown: `Utf8`, `Utf8Bom`, `Ansi`, `Utf16`, `Utf16Be`
- `Top`: jump to the beginning and disable auto-scroll
- `Bottom`: jump to the latest lines and enable auto-scroll
- `Auto-scroll`: keep the viewport pinned to new lines

Manual scrolling disables auto-scroll until you turn it back on or use `Bottom`.

## Organize Dashboards and Folders

The dashboard tree uses two node types:

- `Folder`: organizational only and can contain children
- `Dashboard`: leaf node that owns file memberships

### Create and Manage Items

- Use `+ Folder` and `+ Dashboard` above the tree for quick creation.
- Use the row context menu for `New Folder Here`, `New Dashboard Here`, `Move Up`, `Move Down`, `Delete Item`, `Expand All Folders`, and `Collapse All Folders`.
- Rename an item by double-clicking its name, then press `Enter` to save or `Esc` to cancel.

### Add and Remove Dashboard Files

- Use `Add Files...` on a dashboard row or its context menu.
- Dashboard member files appear under the dashboard in the tree.
- Remove a member file from its context menu.

### Filter by Dashboard

- Clicking a dashboard makes it the only active dashboard filter.
- Clicking the same active dashboard again clears filtering.
- Clicking a folder changes selection only; it does not activate filtering.
- The status bar shows filtered tab counts versus ad-hoc tab counts.

### Search the Tree

Use the dashboard filter box above the tree to filter folders and dashboards by name.

## Search, Filter, and Go To

The right pane contains three tabs: `Search`, `Filter`, and `Go To`.

### Search

Search scope:

- `Current file`
- `All open files`

Search source modes:

- `Disk snapshot`: searches current on-disk content and finishes
- `Tail`: monitors only newly appended lines
- `Snapshot + Tail`: starts tail monitoring and backfills existing file content

Additional controls:

- `Results Line Order`: `Ascending` or `Descending`
- Optional timestamp range: `From` and `To`
- Match options: `Regex`, `Case sensitive`, `Whole word`
- Actions: `Search`, `Cancel`, `Clear`

Shortcuts:

- `Ctrl+F`: run the current search
- `Enter` in the search box: run the current search

Results are grouped by file. Click a hit to open that file and jump to the matching line.

### Filter

Filtering applies to the selected tab only.

Filter behavior:

- Builds the filtered view from the current file snapshot
- Keeps filtered results updated as new tail lines arrive
- Uses the same timestamp parser as Search for optional `From` and `To` values
- Supports `Regex`, `Case sensitive`, and `Whole word`

Actions:

- `Apply Filter`
- `Clear Filter`

Notes:

- You must have a selected tab to apply or clear a filter.
- While a filter is active, the tab shows only matching lines.
- `Enter` in the filter query box applies the current filter.

### Go To

Navigation also applies to the selected tab only.

- `Go To Timestamp`: navigates to an exact timestamp match or the nearest timestamp
- `Go To Line`: navigates to a specific line number

Press `Enter` in either box to run the current navigation command.

## Live Tailing and Rotation

Open tabs are monitored for file growth and rotation:

- New data updates line counts and the viewport when auto-scroll is enabled
- Rotation or truncation reloads the tab

Global auto-tail behavior is controlled by settings and tab visibility.

## Settings

Open settings from the toolbar `Settings` button.

Available settings:

- Default open directory
- Global auto-tail enable or disable
- Default file encoding
- Fallback encoding order, up to three entries
- Log font family
- Line highlight rules

Highlight rules support:

- Enabled toggle
- Text or regex pattern
- Case-sensitive matching
- Preset or custom color selection

Rules are evaluated in order, and the first match wins.

## Import and Export

Dashboard views can be exported and imported as JSON from the main toolbar or File menu.

- Default import and export folder: the active storage root's `Data\Views` folder
- Import replaces the current saved dashboard tree with the selected view
- Missing import files are ignored
- Malformed import files show an error dialog

## Session Persistence

On exit, LogReader saves:

- Open tabs
- Active tab
- Per-tab encoding
- Per-tab auto-scroll
- Per-tab pin state

On next launch, missing files are skipped.

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open log file(s) |
| `Ctrl+F` | Execute search |
| `Ctrl+Left` | Select previous tab |
| `Ctrl+Right` | Select next tab |
| `Ctrl+W` | Close selected tab |
| `Ctrl+1` | Toggle dashboards pane |
| `Ctrl+2` | Toggle search pane |
| `F10` | Toggle focus mode |
| `Enter` (search box) | Execute search |
| `Enter` (filter query box) | Apply filter |
| `Enter` (Go To Timestamp) | Navigate to timestamp |
| `Enter` (Go To Line) | Navigate to line |
| `Esc` (rename text box) | Cancel rename |
