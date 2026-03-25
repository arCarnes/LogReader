# Agent C - Security and Trust Boundaries

## Scope

This pass focused on auth or trust boundaries, unsafe file handling, path validation, command execution, and places where user-controlled input crosses into privileged behavior.

## Findings

### [MEDIUM] Imported dashboard views can trigger unintended network access
- Confidence: High
- Location: `LogReader.Infrastructure/Repositories/JsonLogGroupRepository.cs`, `LogReader.App/Services/DashboardWorkspaceService.cs`
- Evidence: imported `FilePaths` are accepted from JSON and later checked with file-system APIs such as `File.Exists`.
- Why it matters: on Windows, probing a UNC path can authenticate to a remote server and leak credentials or at least create unexpected outbound access.
- Fix direction: reject or explicitly confirm UNC, relative, and other non-local paths before persisting imported views.
- Runtime assumption: this depends on users opening untrusted exports, which is plausible for a support-oriented log tool.

### [MEDIUM] Uninstall cleanup can delete outside the intended storage root if metadata is tampered
- Confidence: Medium
- Location: `LogReader.Setup/InstallerActions.vbs`, `LogReader.Setup/Product.wxs`
- Evidence: uninstall logic reconstructs cleanup targets from mutable persisted path information and deletes app data subfolders without revalidating ownership.
- Why it matters: tampered configuration or installer properties can widen the delete surface during uninstall.
- Fix direction: constrain cleanup to validated app-owned roots and refuse deletion when the resolved path is unexpected.
- Runtime assumption: exploitability depends on local tampering, but the blast radius is still larger than necessary.

## Security Theme

The repo does not show obvious remote-code-execution style issues, but its trust boundaries around imported file paths and uninstall cleanup are too permissive. The main security work here is tightening path validation and treating imported data as untrusted until proven safe.
