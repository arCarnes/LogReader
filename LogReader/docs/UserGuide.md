# LogReader User Guide

Last updated: 2026-04-08

LogReader is a Windows desktop tool for reading, filtering, searching, and tailing log files. This guide assumes the app is already running. For build and launch steps, see the [Developer Guide](./DeveloperGuide.md).

## Main Layout

- Left pane: dashboard tree, including the `Ad Hoc` scope row
- Center: tab strip and log viewer
- Bottom workspace: `Search` and `Filter` under the active reader
- Bottom: status bar with the current scope and visible tab count

The bottom search workspace is resizable with the splitter above it.

Pane shortcuts:

- `Ctrl+1`: toggle dashboards pane
- `F10`: toggle dashboards pane

The top toolbar contains the main file and view actions, including `Open Log Files`, `Bulk Open Files`, `Export View`, `Import View`, `Hotkeys`, and `Settings`.
You can also open the built-in shortcut reference from the toolbar `Hotkeys` button.

## Open Log Files

Open files by:

- Using `Open Log Files` from the toolbar (`Ctrl+O`)
- Using `Bulk Open Files` from the toolbar or `Bulk Open Files...` from the empty reader context menu
- Dragging and dropping files from Explorer
- Clicking a dashboard row to open its member files

`Bulk Open Files` opens an input dialog where you paste one file path or wildcard pattern per line, preview the results, and then confirm. Wildcards are supported in the file-name segment, such as `C:\logs\app-*.log`.

If a file is already open in the same scope, LogReader activates the existing tab instead of opening a duplicate.

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

The strip also includes previous-tab, next-tab, and overflow buttons on the right for quick tab navigation when many files are open.

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

`Auto-scroll (all tabs)` is global. Turning it back on snaps open tabs to the bottom. Scrolling upward or opening a search result turns it off so you can inspect the current location without being pulled back down.

## Organize Dashboards and Folders

The dashboard area contains:

- `Ad Hoc`: a scope row for open tabs that are not assigned to any dashboard
- `Folder`: organizational only and can contain child folders or dashboards
- `Dashboard`: a leaf node that owns file memberships

### Create and Manage Items

- Use `+ Folder` and `+ Dashboard` above the tree for quick creation.
- Use row context menus for item-specific actions such as `New Folder Here`, `New Dashboard Here`, `Add Files...`, `Bulk Open Files...`, `Date Shift`, `Reload Dashboard`, `Move Up`, `Move Down`, and `Delete Item`.
- Use the tree background context menu for `Expand All Folders` and `Collapse All Folders`.
- Rename an item by double-clicking its name, then press `Enter` to save or `Esc` to cancel.
- Drag dashboards and folders within the tree to reorder them or move them under a different folder.

### Add and Remove Dashboard Files

- Use `Add Files...` on a dashboard row to pick files from a file dialog.
- Use `Bulk Open Files...` on a dashboard row to paste one file path or wildcard pattern per line and preview the results before saving them.
- Dashboard member files appear under the dashboard in the tree.
- Missing member files stay listed and show `File not found`.
- Drag a dashboard member file onto another member row to reorder it, or onto another dashboard row to move it between dashboards.
- Right-click a member file for `Copy Full Path`, `Open File Location`, `Reload Dashboard`, `Reload File`, or `Remove from Dashboard`.
- Right-click an `Ad Hoc` member file for `Copy Full Path`, `Open File Location`, or `Close Tab`.

### Switch Scope

- Click a dashboard row to open its member files and make that dashboard the current scope.
- Click the `Ad Hoc` row to return to unassigned open tabs.
- Clicking a folder clears the active dashboard scope without opening files.
- The status bar shows the active scope and the visible-tab count versus total open tabs.
- Right-click the `Ad Hoc` row for `Date Shift` and `Clear Ad Hoc Files`.

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

The modifier uses the ordered date rolling patterns from Settings. When a modifier is active, the dashboard or `Ad Hoc` label shows it until you clear the modifier from the same `Date Shift` menu. If no date rolling patterns are configured, the menu shows a setup message instead of the `T-1` to `T-7` actions.

## Search and Filter

The bottom search workspace stays available under the reader. It contains shared `Target` and `Source` controls, the primary search input, a results list, and a collapsible `Filter` section.

Shortcuts:

- `Ctrl+F`: focus the active `Search` or `Filter` box
- `Enter` in the search box: run the current search
- `Enter` on a selected search result: navigate to that hit
- `Enter` in the filter query box: apply the current filter

### Search

Search and Filter share the top-row target and source controls.

Target:

- `Current tab`
- `Current scope`, which means all tabs currently visible in the active scope

Source modes:

- `Disk`: searches current on-disk content and finishes
- `Tail`: monitors only newly appended lines
- `Snapshot + Tail`: starts tail monitoring and also backfills existing file content

Search controls:

- Match options: `Regex`, `Case`
- Actions: `Search`, then `Cancel` while a search is running or `Clear` when the panel is idle

Search summary text appears in the `SEARCH RESULTS` header instead of repeating under the search controls.

Results are grouped by file. Double-clicking a hit or pressing `Enter` on a selected hit opens that file, jumps to the matching line, and leaves auto-scroll off while you inspect the result.

You can also:

- Double-click a hit to navigate to it
- Press `Ctrl+C` in the results list to copy selected hit lines
- Use the results context menu to collapse the current result set or all result sets

Visible search output is tied to the current tab, scope, target, and source mode. If that context changes, LogReader clears the visible result list and shows a short status instead of leaving stale results on screen. Returning to the original context can restore the cached results for that scope.

### Filter

Filtering lives in the collapsible `Filter` section in the same bottom workspace and uses the shared `Target` and `Source` controls above it.

Filter behavior:

- `Current tab` applies to the selected tab only
- `Current scope` applies across all tabs currently visible in the active scope
- Builds the filtered view from the current tab or scope snapshot
- Keeps filtered results updated as new tail lines arrive
- Uses the app's timestamp parser for optional `From` and `To` values
- Supports `Regex` and `Case`

Actions:

- `Apply`
- `Clear Tab Filter` or `Clear Scope Filter`, depending on the current target

Notes:

- You must have a selected tab when the target is `Current tab`.
- While a filter is active, the tab shows only matching lines.
- Scope filters stay active until you explicitly clear them, and clearing always follows the current target.
- `Enter` in the filter query box applies the current filter.
- Current-scope filtering can surface per-file warnings when some files cannot fully participate in the requested filter.
- If a time range is set and the selected file has no parseable timestamps, the filter status explains that instead of silently showing no matches.

## Live Tailing and Rotation

Open tabs are monitored for file growth and rotation:

- New data updates line counts and the viewport when auto-scroll is enabled
- Rotation or truncation reloads the tab
- `Tail` and `Snapshot + Tail` search modes continue monitoring until you cancel the search

## Settings

Open settings from the toolbar `Settings` button.

Available settings:

- Default open directory
- Log font family and size (`Consolas`, `Cascadia Mono`, `Cascadia Code`, `Lucida Console`, or `Courier New`, size 8-18)
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

Dashboard views can be exported and imported as JSON from the main toolbar.

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
| `Ctrl+F` | Focus the active `Search` or `Filter` box |
| `Ctrl+C` | Copy selected viewer lines or selected search hits |
| `Ctrl+Left` | Select previous visible tab |
| `Ctrl+Right` | Select next visible tab |
| `Ctrl+W` | Close selected tab |
| `Ctrl+1` | Toggle dashboards pane |
| `F10` | Toggle dashboards pane |
| `Enter` (search box) | Execute search |
| `Enter` (search results) | Navigate selected hit |
| `Enter` (filter query box) | Apply filter |
| `Esc` (rename text box) | Cancel rename |
