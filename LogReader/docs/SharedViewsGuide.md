# Saved and shared views

A view is the complete dashboard/folder tree and its log-file memberships. Use the toolbar selector to switch views, or **Views...** to manage them. Your existing dashboard becomes **Default View** on first launch.

## My Views

Enter a unique view name, then choose **New View** for an empty view or **Copy active to My Views** to duplicate the active definition. Both activate the result. To rename or delete, select a local view in the list. Switch away before deleting the active view; keep at least one local view.

Edits are saved as you work. Switching preserves each local definition, closes the outgoing dashboard's tabs and clears its search/filter state, while leaving Ad Hoc tabs open. The last selected view returns on restart; per-view tab sessions are not restored.

**Import View** creates a new local view instead of replacing an existing one. **Export View** still produces the schema-1 JSON format, including explicit file paths.

## Folder sources

In **Views...**, browse to a folder containing `weeztail.json`, optionally enter a display name, and select **Add Folder**. Views appear under that source in the toolbar selector. This also works with a Git checkout you maintain yourself.

Shared definitions are read-only. Searching, filtering and opening their logs still work. To customize one, activate it, enter a new local name, and choose **Copy active to My Views**. The copy does not receive source updates.

Select a source and choose **Refresh** to accept its latest valid definitions. Refreshing an active shared view reloads its definition and closes the outgoing dashboard tabs. There is no automatic refresh. If an update removes the active view, switch away and refresh again.

Invalid or unavailable sources leave the last accepted definitions usable, including after restart. Select a source to see its last successful refresh; operation errors appear at the bottom of the dialog. Removing a source removes only WeezTail's registration and owned data, never the external folder or logs. Switch away from that source first.

## Git sources

Install Git for Windows and configure authentication using your normal Git credential helper or SSH agent outside WeezTail. Enter an HTTPS or SSH remote, optionally use **Load Git Refs**, choose a revision, then select **Add Git Source**.

- **Branch:** blank uses the remote's advertised default branch. Explicit Refresh follows the selected branch's current tip.
- **Tag:** resolves once and stays pinned, even if the tag later moves. Use **Change Revision** to explicitly resolve it again.
- **Commit:** enter a full commit ID available from the fetched repository. It remains pinned.

**Change Revision** applies the entered revision to the selected Git source. The dialog shows its selected reference and accepted commit. WeezTail fetches into a private bare cache and reads ordinary JSON files from the commit. It never checks out a user worktree, runs repository hooks, commits, pushes, or opens pull requests. A damaged cache can be recreated by Refresh without losing accepted definitions.

Git commands are cancellable and time out after two minutes per command. Authentication prompts are disabled. If authentication fails, fix it in your Git client and retry. Do not put passwords or tokens in the remote URL. Git submodules and Git LFS bundle files are unsupported.

## Team repository format

Start with the checked-in [two-view example](examples/shared-views/weeztail.json). Replace its example log paths with your team's paths. The root manifest names the collection and declares each view:

```json
{
  "schemaVersion": 1,
  "name": "Operations",
  "views": [
    { "id": "production", "name": "Production", "path": "views/production.json" },
    { "id": "staging", "name": "Staging", "path": "views/staging.json" }
  ]
}
```

Each referenced file is an ordinary schema-1 view export. Keep manifest view IDs and exported group IDs stable when editing or moving a view; names and filenames can change. To update an existing shared view from a local copy, reconcile group IDs deliberately: local copying assigns new IDs.

Use one repository per team/access boundary, with multiple JSON view files. Edit, review, commit and publish using your team's existing Git workflow, then have members choose Refresh in WeezTail.

File references must be explicit Windows drive paths or UNC paths accessible to teammates. Portable root mappings, wildcards and personal overrides are not supported in shared bundles. Only view definitions are shared: do not commit logs, credentials, private local storage, or machine-specific session data.

Manifest paths must stay inside the source and cannot use links/reparse points. The manifest limit is 1 MiB, each view is limited to 16 MiB, and the full bundle is limited to 64 MiB. One invalid file rejects the whole update.

## Storage and recovery

The library and accepted snapshots live under `Data/Views` in the selected storage root. Git object caches live under the app's per-user cache. Include `Data/Views`, `loggroups.json`, and `logfiles.json` in a coordinated backup.

An interrupted switch is recovered at startup. Before the commit marker, recovery restores the prior active view; after it, recovery completes the new view. If recovery cannot complete, WeezTail preserves the journal and reports the error. Restore a coordinated backup or repair the reported storage problem; do not delete individual recovery files to bypass an error.
