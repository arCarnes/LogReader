# LogReader User Guide

Last updated: 2026-04-01

LogReader is a Windows desktop tool for reading, filtering, searching, and tailing log files. This guide assumes the app is already running. For build and launch steps, see the [Developer Guide](./DeveloperGuide.md).

## Main Layout

- Left pane: dashboard tree, including the `Ad Hoc` scope row
- Center: tabbed log viewer
- Right pane: `Search`, `Filter`, and `Go To`
- Bottom: status bar with the current scope and visible tab count

Pane shortcuts:

- `Ctrl+1`: toggle dashboards pane
- `Ctrl+2`: toggle search pane
- `F10`: reader focus mode, which toggles both side panes together

You can also open the built-in shortcut reference from the toolbar `Hotkeys` button.

## Open Log Files

Open files by:

- Using `Open Log File...` from the toolbar or File menu (`Ctrl+O`)
- Using `Bulk Open Files...` from the toolbar, File menu, or the empty reader context menu
- Dragging and dropping files from Explorer
- Clicking a dashboard row to open its member files

`Bulk Open Files...` opens an input dialog where you paste one literal file path per line, preview the results, and then confirm.

If a file is already open, LogReader activates the existing tab instead of opening a duplicate.

## Work with Tabs

The tab strip only shows tabs in the current scope:

- `Ad Hoc`: open tabs that are not assigned to any dashboard
- `Dashboard`: open tabs that belong to the selected dashboard, including date-shifted members when a modifier is active

Right-click a tab to:

- Pin or unpin it
- Close it
- Close other tabs
- Close all but pinned tabs
- Close all tabs

Pinned tabs are sorted before unpinned tabs in the current visible scope.

Tab shortcuts:

- `Ctrl+W`: close the selected tab
- `Ctrl+Left`: select the previous visible tab
- `Ctrl+Right`: select the next visible tab

## Read and Navigate Logs

Each tab shows line numbers and log text in the configured monospace font.

Viewer behavior:

- Virtualized rendering for large files
- Vertical navigation with the custom scrollbar, mouse wheel, and `Up` / `Down` / `Page Up` / `Page Down` / `Home` / `End`
- Horizontal scrolling for long lines
- Copying selected lines with `Ctrl+C`

Tab toolbar actions:

- Encoding dropdown: `Auto`, `UTF-8`, `UTF-16`, `UTF-16 BE`, `ANSI`
- `Apply To All`: copy the selected tab's encoding choice to every open tab
- `Top`: jump to the beginning of the current view
- `Bottom`: jump to the latest lines in the selected tab
- `Auto-scroll (all tabs)`: keep open tabs pinned to the logical bottom as new data arrives

`Auto-scroll (all tabs)` is global. Turning it back on snaps open tabs to the bottom. Scrolling upward, using `Go To`, or opening a search result turns it off so you can inspect the current location without being pulled back down.

## Organize Dashboards and Folders

The dashboard area contains:

- `Ad Hoc`: a scope row for open tabs that are not assigned to any dashboard
- `Folder`: organizational only and can contain child folders or dashboards
- `Dashboard`: a leaf node that owns file memberships

### Create and Manage Items

- Use `+ Folder` and `+ Dashboard` above the tree for quick creation.
- Use a row context menu for `New Folder Here`, `New Dashboard Here`, `Add Files...`, `Bulk Open Files...`, `Date Shift`, `Move Up`, `Move Down`, `Delete Item`, `Expand All Folders`, and `Collapse All Folders`.
- Rename an item by double-clicking its name, then press `Enter` to save or `Esc` to cancel.
- Drag dashboards and folders within the tree to reorder them or move them under a different folder.

### Add and Remove Dashboard Files

- Use `Add Files...` on a dashboard row to pick files from a file dialog.
- Use `Bulk Open Files...` on a dashboard row to paste one literal path per line and preview the results before saving them.
- Dashboard member files appear under the dashboard in the tree.
- Missing member files stay listed and show `File not found`.
- Right-click a member file for `Copy Full Path`, `Open File Location`, or `Remove from Dashboard`.

### Switch Scope

- Click a dashboard row to open its member files and make that dashboard the current scope.
- Click the `Ad Hoc` row to return to unassigned open tabs.
- Clicking a folder clears the active dashboard scope without opening files.
- The status bar shows the active scope and the visible-tab count versus total open tabs.

### Search the Tree

Use the dashboard filter box above the tree to filter folders and dashboards by name.

### Date Shift

Use `Date Shift` from a dashboard row or the `Ad Hoc` row to apply one of the built-in modifiers:

- `T-1`
- `T-2`
- `T-3`
- `T-4`
- `T-5`
- `T-6`
- `T-7`

The modifier uses the ordered date rolling patterns from Settings. When a modifier is active, the dashboard or `Ad Hoc` label shows it until you clear the modifier.

## Search, Filter, and Go To

The right pane contains three tabs: `Search`, `Filter`, and `Go To`.

### Search

Search and Filter share the top-row target and source controls.

Target:

- `Current tab`
- `Current scope`, which means all tabs currently visible in the active scope

Source modes:

- `Disk snapshot`: searches current on-disk content and finishes
- `Tail`: monitors only newly appended lines
- `Snapshot + Tail`: starts tail monitoring and also backfills existing file content

Additional controls:

- `Results Line Order`: `Ascending` or `Descending`
- Optional timestamp range: `From` and `To`
- Match options: `Regex`, `Case sensitive`, `Whole word`
- Actions: `Search`, `Cancel`, `Clear`

Shortcuts:

- `Ctrl+F`: run the current search
- `Enter` in the search box: run the current search

Results are grouped by file. Clicking a hit opens that file, jumps to the matching line, and leaves auto-scroll off while you inspect the result.

If you apply a timestamp range and none of the target files contain parseable timestamps, the status text tells you that directly.

### Filter

Filtering uses the shared `Target` and `Source` controls above the panels.

Filter behavior:

- `Current tab` applies to the selected tab only
- `Current scope` applies across all tabs currently visible in the active scope
- Builds the filtered view from the current tab or scope snapshot
- Keeps filtered results updated as new tail lines arrive
- Uses the same timestamp parser as Search for optional `From` and `To` values
- Supports `Regex`, `Case sensitive`, and `Whole word`

Actions:

- `Apply Filter`
- `Clear Filter`

Notes:

- You must have a selected tab when the target is `Current tab`.
- While a filter is active, the tab shows only matching lines.
- `Enter` in the filter query box applies the current filter.
- If a time range is set and the selected file has no parseable timestamps, the filter status explains that instead of silently showing no matches.

### Go To

Navigation applies to the selected tab only.

- `Go To Timestamp`: jumps to an exact timestamp match or the nearest timestamp
- `Go To Line`: jumps to a specific line number

Accepted timestamp formats:

- `2026-03-09T19:49:20Z`
- `2026-03-09 19:49:20`
- `19:49:20.123`

Press `Enter` in either box to run the current navigation command.

## Live Tailing and Rotation

Open tabs are monitored for file growth and rotation:

- New data updates line counts and the viewport when auto-scroll is enabled
- Rotation or truncation reloads the tab
- `Tail` and `Snapshot + Tail` search modes continue monitoring until you cancel the search

## Settings

Open settings from the toolbar `Settings` button.

Available settings:

- Default open directory
- Log font family
- Dashboard file labels, including showing full paths when space allows
- Line highlight rules
- Date rolling patterns

Highlight rules support:

- Enabled toggle
- Text or regex pattern
- Case-sensitive matching
- Preset or custom color selection

Rules are evaluated in order, and the first match wins.

## Date Rolling Patterns

Open `Settings` and use the `Date Rolling Patterns` section.

- Each pattern has a required `Name`, plus `Find` and `Replace` values.
- `Replace` must include at least one date placeholder such as `{yyyyMMdd}` or `{yyyy-MM-dd_HHmmss}`.
- Placeholders use standard .NET / C# date format strings inside braces.
- Invalid names or placeholder syntax are highlighted in the dialog and must be fixed before saving.
- Patterns are tried from top to bottom, so the list order defines fallback precedence.
- Use the arrow buttons to reorder patterns.
- These patterns power the dashboard and `Ad Hoc` `Date Shift` actions.

## Import and Export

Dashboard views can be exported and imported as JSON from the main toolbar or File menu.

- Default import and export folder:
  - Portable install: `Data\Views` beside `LogReader.exe`
  - MSI install: `<selected storage folder>\Data\Views`
- Import can prompt you to export the current dashboard tree first.
- Import replaces the current saved dashboard tree with the selected view.
- UNC paths in imported views are allowed.
- Relative, drive-relative, and device-prefixed paths trigger a trust warning before import.
- Malformed import files show an error dialog.

## Saved Data

LogReader saves its dashboard tree, known file catalog, and settings under the app data folder for the current install mode.

On the next launch:

- Settings and dashboards are reloaded.
- Missing dashboard member files stay registered and continue to show as missing.
- Open tabs are not reopened automatically.

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open log file(s) |
| `Ctrl+F` | Execute search |
| `Ctrl+C` | Copy selected viewer lines |
| `Ctrl+Left` | Select previous visible tab |
| `Ctrl+Right` | Select next visible tab |
| `Ctrl+W` | Close selected tab |
| `Ctrl+1` | Toggle dashboards pane |
| `Ctrl+2` | Toggle search pane |
| `F10` | Toggle focus mode |
| `Enter` (search box) | Execute search |
| `Enter` (filter query box) | Apply filter |
| `Enter` (Go To Timestamp) | Navigate to timestamp |
| `Enter` (Go To Line) | Navigate to line |
| `Esc` (rename text box) | Cancel rename |
