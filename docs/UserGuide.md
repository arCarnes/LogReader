# LogReader User Guide

LogReader is a Windows desktop application for reading, searching, and monitoring large log files in real time.

---

## Table of Contents

- [Getting Started](#getting-started)
- [Log Viewer](#log-viewer)
- [Tab Management](#tab-management)
- [Navigation](#navigation)
- [Encoding](#encoding)
- [Live Tailing](#live-tailing)
- [Groups](#groups)
- [Search](#search)
- [Line Highlighting](#line-highlighting)
- [Settings](#settings)
- [Keyboard Shortcuts](#keyboard-shortcuts)
- [Session Persistence](#session-persistence)
- [Status Bar](#status-bar)

---

## Getting Started

### Opening Files

There are several ways to open log files:

- **Toolbar**: Click the **Open** button (or press **Ctrl+O**) to open a file dialog. You can select multiple files at once.
- **Drag and Drop**: Drag files from Windows Explorer onto the LogReader window.
- **Groups**: Click a group header to open all files belonging to that group (see [Groups](#groups)).

The file dialog filters for `.log` and `.txt` files by default, but you can switch to "All Files" to open any file type.

If you open a file that is already open, LogReader switches to its existing tab instead of opening a duplicate.

### Default Open Directory

You can set a default directory for the file open dialog in **Settings**. This saves time if your log files are always in the same location.

---

## Log Viewer

Each open file is displayed in its own tab. The log viewer shows:

- **Line numbers** on the left (right-aligned, gray)
- **Line content** in a monospace font (Consolas, 12pt)

The viewer is virtualized, meaning it only renders the lines currently visible on screen. This allows LogReader to handle files with millions of lines without excessive memory usage.

Horizontal scrolling is available when lines exceed the viewport width.

---

## Tab Management

Right-click any tab header to open the context menu with the following options:

| Menu Item | Action |
|-----------|--------|
| **Pin Tab** / **Unpin Tab** | Toggles the pin state. Pinned tabs show a 📌 icon and are sorted to the left of unpinned tabs. |
| **Close** | Closes this tab. |
| **Close Others** | Closes all tabs except this one. |
| **Close All But Pinned** | Closes all unpinned tabs, keeping only pinned tabs open. |
| **Close All** | Closes every open tab. |

### Pinned Tabs

Pinned tabs are always displayed first (leftmost) in the tab bar. Pin state is persisted across sessions, so your pinned tabs remain pinned when you restart LogReader.

You can also close the current tab with **Ctrl+W**.

---

## Navigation

### Scrolling

- **Mouse wheel**: Scrolls 3 lines per tick
- **Scrollbar**: The vertical scrollbar on the right side spans the entire file. Drag the thumb or click to jump to any position.
- **Page up/down**: Click the scrollbar track above or below the thumb to move 50 lines at a time.

### Jump Buttons

Located in the per-tab toolbar:

- **Top**: Jumps to the first line of the file and disables auto-scroll.
- **Bottom**: Jumps to the end of the file and enables auto-scroll (see below).

### Auto-Scroll

The **Auto-scroll** checkbox pins the view to the bottom of the file. When enabled, new lines appended to the file will automatically scroll into view.

- Scrolling manually (mouse wheel or scrollbar) **disables** auto-scroll.
- Clicking **Bottom** **enables** auto-scroll.
- Clicking **Top** **disables** auto-scroll.

---

## Encoding

Each tab has encoding radio buttons in its toolbar:

| Encoding | Description |
|----------|-------------|
| **UTF-8** | Standard Unicode encoding (default) |
| **ANSI** | Windows-1252 encoding, common in older Windows applications |
| **UTF-16** | 16-bit Unicode with byte order mark detection |

Switching the encoding reloads the file from disk and restarts live tailing. Choose the encoding that matches how your log file was written.

---

## Live Tailing

LogReader automatically monitors open files for changes. When new lines are appended to a log file, they appear in the viewer in real time (polled every 250ms).

### File Rotation

LogReader detects log file rotation (common with logging frameworks that archive old logs and start a new file). When rotation is detected, the file is automatically reloaded from scratch. Rotation is detected by:

- File creation time changing (new file created with the same name)
- File size shrinking (file was truncated)
- File temporarily disappearing and reappearing

---

## Groups

Groups let you organize related log files together. The Groups panel is on the left side of the window.

### Creating a Group

Click the **+** button in the Groups panel header. A new group named "New Group" is created with a color automatically assigned.

### Renaming a Group

Double-click the group name to enter edit mode. Type the new name and press **Enter** to save, or **Escape** to cancel.

### Managing Group Files

Click the **Manage** button on a group to open the file management dialog. This shows all currently open files with checkboxes. Check the files you want in the group and click **OK**.

You can also use **Select All** and **Deselect All** buttons for convenience.

### Reordering Groups

Use the **arrow** buttons on each group row to move it up or down in the list. The order is persisted across sessions.

### Filtering by Group

Click a group header to **select** it. When a group is selected:

- Only tabs belonging to that group are shown in the tab bar.
- The status bar shows "X of Y tabs (filtered)".

Click the same group again to **deselect** it and show all tabs.

**Multi-select:** Hold **Ctrl** and click to select multiple groups at once. All files from all selected groups are shown. A plain click (without Ctrl) selects only that group and deselects all others.

### Opening Group Files

When you click a group, any files in the group that aren't already open will be opened automatically.

### Export and Import

- **Export**: Click the **Export** button on a group to save it as a JSON file. This preserves the group name and file paths.
- **Import**: Click the **Import** button in the Groups panel header to load a previously exported group. Files are matched by path.

### Deleting a Group

Click the red **X** button on a group to delete it. This only removes the group definition; it does not close any open tabs or delete any files.

---

## Search

The Search panel is on the right side of the window.

### Running a Search

1. Type your query in the search box.
2. (Optional) Enable search options:
   - **Regex**: Treat the query as a regular expression.
   - **Case sensitive**: Match exact letter casing.
   - **Whole word**: Only match complete words, not substrings.
3. Choose a **Search in** scope:
   - **Current file**: Search only the active tab.
   - **All open files**: Search every open tab.
   - **Group: [name]**: Search all files belonging to a specific group.
4. Click **Search** or press **Enter**.

### Search Results

Results are grouped by file. Each file section shows:

- The filename and number of matches.
- A list of matching lines, each showing the line number and text.

Click any result line to navigate directly to that line in the log viewer. If the file isn't open, it will be opened first.

### Cancelling and Clearing

- Click **Cancel** while a search is running to stop it.
- Click **Clear** to remove all results from the panel.

A progress bar is shown while searching.

---

## Line Highlighting

Line highlighting applies background colors to log lines matching specific patterns. This is useful for visually distinguishing errors, warnings, or other important log entries.

### Configuring Rules

Open **Settings** and scroll to the **Line Highlighting** section.

1. Click **+ Add Rule** to create a new rule.
2. Configure the rule:
   - **Enabled** checkbox: Toggle the rule on/off without deleting it.
   - **Pattern**: The text or regex pattern to match.
   - **Regex** checkbox: Treat the pattern as a regular expression.
   - **Case** checkbox: Match case-sensitively.
   - **Color**: Choose from preset swatches (Red, Orange, Yellow, Green) or click **...** to pick a custom color.
3. Click **OK** to apply.

### How Matching Works

- Rules are evaluated **in order** from top to bottom.
- The **first matching rule** determines the line's background color.
- If no rules match, the line has no highlight (default background).
- Invalid regex patterns are silently skipped.

Changes apply immediately to all open tabs when you click OK in Settings.

---

## Settings

Access settings via the **Settings** button in the toolbar.

### General

- **Default open directory**: Set the folder that the file open dialog starts in. Use **Browse** to select a folder, or **Clear** to reset it.

### Line Highlighting

See [Line Highlighting](#line-highlighting) above for details.

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| **Ctrl+O** | Open file(s) |
| **Ctrl+F** | Focus search box |
| **Ctrl+W** | Close current tab |
| **Enter** | Execute search (when search box is focused) |
| **Ctrl+Click** | Multi-select groups (toggle without deselecting others) |
| **Escape** | Cancel group name editing |

---

## Session Persistence

LogReader saves your session when you close the application:

- All open tabs (file paths, encodings, auto-scroll state, pin state)
- The currently active tab

On next launch, your previous session is restored automatically. Files that no longer exist on disk are skipped.

---

## Status Bar

The status bar at the bottom of the window shows:

- **Tab count**: "X tabs open" when no group is selected.
- **Filtered count**: "X of Y tabs (filtered)" when a group is selected.

Each tab also has its own status area showing:

- **Line count and file size** (left side)
- **Full file path** (right side)
