# LogReader User Guide

Last updated: 2026-03-11

LogReader is a Windows desktop tool for reading, filtering, searching, and tailing log files.

## Main Layout

- Left pane: dashboard/folder tree
- Center: tabbed log viewer
- Right pane: search panel
- Bottom: status bar

Use these shortcuts to control side panes:

- `Ctrl+1`: toggle dashboards pane
- `Ctrl+2`: toggle search pane
- `F10`: focus mode (toggle both panes together)

## Opening Files

You can open files by:

- Toolbar or menu: `Open Log File...` (`Ctrl+O`)
- Drag and drop from Explorer
- Clicking a dashboard row to open its member files

If a file is already open, LogReader activates the existing tab instead of opening a duplicate.

## Tab Workflow

Right-click a tab for:

- Pin/Unpin tab
- Close
- Close others
- Close all but pinned
- Close all

Pinned tabs are sorted before unpinned tabs and are persisted in session state.

`Ctrl+W` closes the currently selected tab.

Tab navigation shortcuts:

- `Ctrl+Left`: select previous visible tab
- `Ctrl+Right`: select next visible tab

## Log Viewer Basics

Each tab shows:

- Line numbers
- Log text (monospace font)

Viewer behavior:

- Virtualized rendering for large files
- Vertical navigation via custom scrollbar and mouse wheel
- Horizontal scrolling for long lines

Tab toolbar actions:

- Encoding dropdown (`Utf8`, `Utf8Bom`, `Ansi`, `Utf16`, `Utf16Be`)
- `Top` button: jump to first lines and disable auto-scroll
- `Bottom` button: jump to latest lines and enable auto-scroll
- `Auto-scroll` checkbox

Manual scroll input disables auto-scroll until re-enabled.

## Dashboards and Folders

The left pane supports a tree with two node kinds:

- `Folder` (organizational only, can have children)
- `Dashboard` (leaf node, owns file memberships)

### Create and Manage Nodes

- Top buttons: `+ Folder` and `+ Dashboard`
- Row context menu:
  - New Folder Here
  - New Dashboard Here
  - Move Up / Move Down
  - Export Item
  - Delete Item
  - Expand All Folders / Collapse All Folders

Rename any item by double-clicking its name, then:

- `Enter` to save
- `Esc` to cancel

### Add and Remove Dashboard Files

- Use `Add Files...` on a dashboard row/context menu.
- File members are shown under the dashboard.
- Remove a member file from its context menu.

### Filtering and Selection

- Clicking a dashboard activates it as the only active filter.
- Clicking the same active dashboard again clears filtering.
- Clicking a folder does not activate filtering.
- The status bar reflects filtered vs ad-hoc tab counts.

### Tree Search

Use the dashboard filter box above the tree to filter folders/dashboards by name.

## Search and Filter Pane

The right pane has two tabs: `Search` and `Filter`.

### Search Tab

Search scope:

- `Current file`
- `All open files`

Search source modes:

- `Disk snapshot`: searches current on-disk content and finishes
- `Tail`: monitors only newly appended lines
- `Snapshot + Tail`: starts tail monitoring and backfills existing file content

Additional search controls:

- `Results Line Order`: `Ascending` or `Descending`
- Optional timestamp range: `From` / `To`
- `Go To Timestamp`: navigates the selected tab to exact match or nearest timestamp
- Match options: `Regex`, `Case sensitive`, `Whole word`
- Actions: `Search`, `Cancel`, `Clear`

Shortcuts:

- `Ctrl+F`: execute search with current query/options
- `Enter` inside search box: execute search

Results are grouped by file. Click a hit to open/navigate to that file and line.

### Filter Tab

Filter applies to the selected tab only.

Filter behavior:

- Builds filtered view from the current file snapshot
- Keeps filtered results updated as new tail lines arrive for that tab
- Optional timestamp range (`From` / `To`) uses the same timestamp parser as Search
- Match options: `Regex`, `Case sensitive`, `Whole word`
- Actions: `Apply Filter`, `Clear Filter`

Notes:

- You must have a selected tab to apply or clear a filter.
- While a filter is active, the tab shows only matching lines.

## Live Tailing and Rotation

Open tabs are monitored for file growth and rotation:

- New data updates line count and viewport (if auto-scroll is on)
- Rotation/truncation triggers tab reload

Global auto-tail behavior is controlled by settings and tab visibility.

## Settings

Open from the toolbar `Settings` button.

Available settings:

- Default open directory
- Global auto-tail enable/disable
- Default file encoding
- Fallback encoding order (up to 3)
- Log font family
- Line highlight rules

Highlight rules support:

- Enabled toggle
- Text or regex pattern
- Case-sensitive option
- Color selection (preset or custom)

Rules are evaluated in order. First match wins.

## Session Persistence

On exit, LogReader saves:

- Open tabs
- Active tab
- Per-tab encoding
- Per-tab auto-scroll
- Per-tab pin state

On next launch, missing files are skipped.

## Import and Export

Dashboards can be exported/imported as JSON.

- Missing import file: operation is ignored
- Malformed import file: error dialog is shown

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
| `Esc` (rename textbox) | Cancel rename |
