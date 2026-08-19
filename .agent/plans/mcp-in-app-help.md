# In-app MCP help and documentation access — Execution Plan

This is a living document. Keep `Progress`, `Surprises & Discoveries`,
`Decision Log`, and `Outcomes & Retrospective` current throughout execution.

## Document control

- Created: 2026-08-18
- Branch: `codex/mcp-log-server`
- Owner: Codex

## Resume checkpoint

- HEAD at plan creation: `73a03f4`
- Working tree at plan creation: clean
- Next action: implement the MCP help presentation model, dialog service, and owned WPF window described in the first milestone.

## Purpose and observable outcome

Make the optional WeezTail MCP server discoverable and understandable from the installed desktop application without turning the application into a second documentation system.

After completion, a user can select **MCP Server** from the main toolbar, see whether the packaged sidecar is present, confirm that WeezTail storage and saved dashboards are ready for agent use, copy the exact sidecar path, understand the five MCP operations at a non-technical level, and open the canonical detailed guide in the default browser. The repository guide remains the authoritative source for Codex and Claude Code commands, protocol details, limits, security guidance, and troubleshooting.

## Scope

- Add an **MCP Server** entry to the existing main-window toolbar beside **Hotkeys** and **Settings**.
- Add an owned, modal WPF help window with three audience layers:
  - Getting started
  - How agent log access works
  - Technical details
- Show a small readiness summary for the sibling `WeezTail.Mcp.exe`, initialized storage, and the number of saved dashboards represented by the loaded tree.
- Provide **Copy server path**, **Open full guide**, and **Close** actions.
- Open the canonical guide through the Windows default browser and report launch failures without crashing WeezTail.
- Reorganize the existing MCP getting-started Markdown guide around the same three layers while keeping the Markdown guide authoritative.
- Add focused automated coverage, run the required build/tests, and manually demonstrate the window in a development and/or published layout.

## Non-goals

- Start, stop, configure, probe, or send MCP requests to `WeezTail.Mcp.exe` from the WPF process.
- Add an MCP SDK or `LogReader.Mcp` project reference to `LogReader.App`.
- Install or edit Codex or Claude Code configuration on the user's behalf.
- Embed the complete Markdown guide, a browser control, or a Markdown renderer in the application.
- Ship a local documentation bundle or alter MSI/portable package contents beyond the existing executables and configuration.
- Add a first-run prompt, notification, telemetry, or recurring MCP reminder.
- Copy client-specific setup commands from the application; those commands can evolve independently and remain in the canonical guide.
- Expose storage roots, physical log paths, log contents, credentials, or other MCP-private implementation state in the help window.

## Definitions

- **Sidecar available**: `WeezTail.Mcp.exe` exists as a file beside the running `WeezTail.exe`, resolved from `AppContext.BaseDirectory`.
- **Storage ready**: normal WPF startup completed its existing storage initialization and reached the main window. The help feature does not independently reopen or revalidate storage configuration.
- **Saved dashboard count**: the count of loaded `LogGroupKind.Dashboard` nodes across the complete root/child group tree, excluding branch/folder nodes and Ad Hoc files.
- **Canonical guide**: `LogReader/docs/McpGettingStarted.md` in the repository, initially opened at `https://github.com/arCarnes/LogReader/blob/main/LogReader/docs/McpGettingStarted.md` until a dedicated documentation site replaces that URL.
- **Readiness summary**: explanatory local state only; it is not an MCP health check and must be labeled accordingly.

## Existing behavior and evidence

- `LogReader/LogReader.App/Views/MainWindow.xaml` exposes text toolbar buttons for **Hotkeys** and **Settings** but no general Help, About, or MCP entry.
- `MainWindow.xaml.cs` opens `ControlsWindow` as an owned modal dialog and delegates Settings through `MainViewModel`, establishing the current interaction pattern.
- `LogReader/LogReader.App/Services/UiAbstractions.cs` centralizes testable dialog services and owner assignment for settings and other modal windows.
- `MainViewModel.Groups` contains the loaded root group collection; `LogGroupViewModel.Children` and `LogGroupViewModel.Kind` provide the recursive tree and dashboard distinction required for a saved-dashboard count.
- `AppPaths` completes storage resolution before normal application composition reaches the main window, so a visible main window is evidence that startup storage setup succeeded.
- MSI and portable packaging place `WeezTail.exe` and `WeezTail.Mcp.exe` in the same directory. `Product.wxs` and `Validate-PortableArtifact.ps1` enforce that layout.
- Debug builds intentionally do not reference or automatically build `LogReader.Mcp`, so the help window must render a useful “not found beside this build” state rather than treating it as a fatal product error.
- `LogReader/docs/McpGettingStarted.md` already contains verified Codex and Claude Code commands, a first-search example, operational notes, and troubleshooting.
- `LogReader.Tests/MainWindowTests.cs`, `SettingsLayoutTests.cs`, `UiAbstractionsTests.cs`, and `UiTestDoubles.cs` provide the nearest existing test seams for toolbar layout, delegated UI actions, owner assignment, and forbidden/stub UI services.

## Decisions and invariants

- Keep `WeezTail.exe` independent from `LogReader.Mcp` and the MCP SDK. The UI uses only the well-known sibling filename and static explanatory content.
- Use one owned modal window instead of a settings tab, startup wizard, or embedded browser. MCP help is discoverable product help, not a persistent application preference.
- Keep the in-app copy short and stable. Client commands, detailed limits, security analysis, and troubleshooting live in Markdown and are opened externally.
- Use three visible sections in one scrollable window. “Getting started” is prominent; plain-language tool behavior and technical details remain immediately available without requiring internet access.
- Display only status labels, the sidecar executable path, and a dashboard count. Do not display the storage root or configured physical log paths.
- Resolve the executable path with `Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "WeezTail.Mcp.exe"))`; do not search `PATH`, the registry, other install directories, or running processes.
- Treat a missing sibling executable as an informative unavailable state. This is expected in some source/debug layouts and must not block the rest of the help content.
- Launch only a compile-time HTTPS documentation URL with `ProcessStartInfo.UseShellExecute = true`. Do not accept a user-provided URL or route it through a shell command string.
- Catch browser-launch and clipboard exceptions, keep the help window open, and show a concise owned error message with useful context but no stack trace.
- Add a narrow MCP-help dialog/action abstraction or equivalent test seam consistent with `UiAbstractions.cs`; do not put process launching or clipboard mutation into core/infrastructure projects.
- Count dashboards with a small deterministic recursive helper and cover nested folders in tests.
- Keep the toolbar label **MCP Server**. Avoid a broader Help menu until WeezTail has additional help destinations that justify one.
- Make documentation URL ownership explicit in one constant so a future docs-site migration is a one-line product change.
- After each coherent implementation milestone, stage only its files and create a local commit as required by repository instructions.

## Open questions

- Confirm the GitHub `main` guide URL is accessible to intended packaged-app users before release. If it is not public or stable enough, replace the centralized URL with the future documentation-site URL; the in-app offline summary still remains functional.
- During visual QA, confirm whether the current toolbar has enough width at the minimum practical window size. If not, shorten only the tooltip/button presentation without introducing a full menu redesign.

## Milestones / issue summary

1. Build the MCP help presentation and modal window.
2. Integrate the help surface with the main toolbar and testable UI flow.
3. Align the canonical documentation with the in-app information architecture.
4. Validate behavior, layout, packaging assumptions, and documentation links.

## Progress

- [x] Read the repository execution-plan contract.
- [x] Inspected the existing main toolbar, modal-window pattern, UI abstractions, dashboard tree model, packaging layout, relevant tests, and MCP documentation.
- [x] Recorded product boundaries and a concrete implementation/validation sequence.
- [ ] Implement the MCP help presentation and window.
- [ ] Integrate the main-window entry and action flow.
- [ ] Update the canonical guide and related links as needed.
- [ ] Run focused and full validation, perform visual/manual QA, and record evidence.

## MCP help presentation and modal window

- State: PLANNED
- Dependencies: existing WPF theme resources, `MainViewModel.Groups`, packaged sibling-executable invariant
- Purpose: provide useful offline orientation and accurate local readiness information without launching the server
- Expected implementation areas:
  - `LogReader/LogReader.App/Views/McpHelpWindow.xaml`
  - `LogReader/LogReader.App/Views/McpHelpWindow.xaml.cs`
  - a focused presentation model/helper under `LogReader.App/ViewModels` or `LogReader.App/Services`
  - `LogReader/LogReader.Tests` focused MCP-help tests
- Tasks:
  - Define immutable presentation data for sidecar path/availability, storage-ready text, dashboard count, canonical guide URL, and the stable explanatory copy.
  - Implement deterministic sibling-path resolution and recursive saved-dashboard counting with injectable or pure seams for tests.
  - Build a themed, scrollable, owned modal window sized consistently with `ControlsWindow` and `SettingsWindow`.
  - Add a readiness card clearly labeled as a local setup summary rather than a live server health check.
  - Add the three sections: getting started, plain-language `folder > dashboard > file` and five-tool behavior, and concise technical/security details.
  - Add **Copy server path**, **Open full guide**, and **Close** actions with accessible tooltips and keyboard/default-button behavior.
  - Handle missing executable, clipboard failure, and browser-launch failure without closing or destabilizing the main application.
- Acceptance criteria:
  - The window remains useful with or without the sibling MCP executable and without network access.
  - The displayed executable path is absolute and always points beside the running WPF executable.
  - Nested dashboard counts are correct and exclude folders/branches and Ad Hoc state.
  - No storage root, physical log path, credentials, or log text appears.
  - Static copy accurately describes the existing five read-only tools and process boundary.
  - The full-guide action can only open the compiled HTTPS guide URL.
- Focused validation:
  - Unit tests for sibling-path resolution, executable present/missing states, and nested dashboard counting.
  - Layout/content assertions for the three headings, readiness labels, and three actions.
  - Action tests using stubs/delegates so automated tests never mutate the real clipboard or open a browser.
- Progress/evidence: not started

## Main-window integration and UI service flow

- State: PLANNED
- Dependencies: completed MCP help window and presentation builder
- Purpose: make the feature discoverable while preserving the existing testable modal-window conventions
- Expected implementation areas:
  - `LogReader/LogReader.App/Views/MainWindow.xaml`
  - `LogReader/LogReader.App/Views/MainWindow.xaml.cs`
  - `LogReader/LogReader.App/Services/UiAbstractions.cs`
  - `LogReader/LogReader.App/ViewModels/MainViewModel.cs` and a suitable partial for the open-help action
  - `LogReader/LogReader.Tests/UiTestDoubles.cs`
  - `LogReader/LogReader.Tests/ForbiddenUiServiceTests.cs`
  - `LogReader/LogReader.Tests/MainWindowTests.cs`, `MainViewModelTests.cs`, `SettingsLayoutTests.cs`, and/or `UiAbstractionsTests.cs`
- Tasks:
  - Add the **MCP Server** toolbar button adjacent to **Hotkeys** and **Settings**, with a tooltip describing the help/setup purpose.
  - Introduce the smallest dialog-service/request seam needed to create presentation data, assign the current main window as owner, and show the modal window.
  - Delegate the click through the existing view/view-model UI-action pattern rather than embedding product logic in XAML.
  - Extend production composition defaults and test doubles without changing public Core APIs.
  - Ensure repeated opens rebuild readiness data so a sidecar copied into place or a changed dashboard tree is reflected.
- Acceptance criteria:
  - Selecting **MCP Server** opens exactly one owned modal help window.
  - Closing the window returns to the existing main window without changing application, dashboard, tab, or MCP state.
  - The toolbar remains usable at supported window sizes and follows current button styling.
  - Existing MainWindow, settings, startup, and forbidden-UI tests continue to pass.
- Focused validation:
  - `dotnet build LogReader.Tests\LogReader.Tests.csproj --no-restore -m:1`
  - `dotnet test LogReader.Tests\LogReader.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~McpHelp|FullyQualifiedName~MainWindow|FullyQualifiedName~UiAbstractions|FullyQualifiedName~ForbiddenUiService|FullyQualifiedName~SettingsLayout"`
  - Manual open/close, focus ownership, keyboard, narrow-window, missing-sidecar, and present-sidecar checks.
- Progress/evidence: not started

## Canonical guide alignment

- State: PLANNED
- Dependencies: final in-app copy and action labels
- Purpose: maintain one authoritative detailed guide while making the browser destination match the in-app mental model
- Expected implementation areas:
  - `LogReader/docs/McpGettingStarted.md`
  - `LogReader/docs/McpLogServerGuide.md`
  - `LogReader/docs/UserGuide.md` and/or `README.md` only if navigation would otherwise be unclear
- Tasks:
  - Reorganize the getting-started guide into explicit **Getting started**, **How agent log access works**, and **Technical reference** sections.
  - Preserve the already verified Codex and Claude Code commands, first-search example, safety guidance, and troubleshooting.
  - Document the new in-app discovery path and clarify that readiness is local setup information, not a live MCP protocol check.
  - Keep detailed limits and threat-model material linked rather than duplicated.
  - Verify every local Markdown link and the centralized external guide URL.
- Acceptance criteria:
  - A non-technical reader can understand hierarchy and agent behavior before encountering protocol terminology.
  - A technical reader can find client commands, tool names, process lifecycle, limits, and security references quickly.
  - The in-app text and guide do not contradict each other or create competing setup instructions.
- Focused validation:
  - Resolve every local link in modified Markdown files.
  - `git diff --check`
  - No runtime validation solely for content-only edits; runtime validation remains required for the combined feature.
- Progress/evidence: not started

## Validation, demonstration, and release evidence

- State: PLANNED
- Dependencies: all implementation and documentation milestones complete
- Purpose: prove the feature works without regressing the WPF application or MCP sidecar boundary
- Expected implementation areas: tests, plan evidence, and only defect fixes discovered during validation
- Tasks:
  - Run the narrow MCP-help tests first and repair any failures.
  - Build the solution and run the full test suite using the repository-prescribed order.
  - Confirm `LogReader.App.csproj` still has no `LogReader.Mcp` or MCP SDK reference.
  - Confirm MSI and portable definitions still place both executables together without adding documentation files.
  - Manually inspect the window at normal and constrained sizes, in light/dark themes if supported by current resources, with the sidecar present and absent.
  - Verify **Copy server path** produces the exact quoted-independent path text expected for client configuration.
  - Verify **Open full guide** uses the default browser in an interactive run and that a simulated failure is handled in automated coverage.
  - Update this plan's evidence, decisions, discoveries, outcomes, and resume checkpoint.
- Acceptance criteria:
  - All required builds and tests pass with no new warnings treated as failures by the existing configuration.
  - Manual demonstration satisfies every observable outcome without starting an MCP process.
  - The WPF dependency graph and ordinary startup remain MCP-SDK-free.
  - The working tree contains only intended changes, and each coherent change has a local commit.
- Focused validation:
  - From `LogReader/`: `dotnet build LogReader.sln -m:1`
  - From `LogReader/`: `dotnet test LogReader.sln --no-build`
  - `git diff --check`
  - Existing portable package layout assertions or `packaging/scripts/Validate-PortableArtifact.ps1` against an available artifact; republish only if implementation or validation shows the sibling-layout assumption is not already covered.
- Progress/evidence: not started

## Final validation and demonstration

The final implementation is complete only when:

1. A packaged or representative sibling-executable layout reports the MCP server as available and copies the exact absolute path.
2. A development layout without `WeezTail.Mcp.exe` reports an informative unavailable state while all explanatory content and the guide link remain usable.
3. A nested saved tree reports the correct dashboard count.
4. The in-app sections explain setup, hierarchy/tool behavior, and technical boundaries without duplicating the full guide.
5. Browser and clipboard failures are recoverable and visible to the user.
6. Focused tests, full solution build, and full solution tests pass.
7. Documentation links resolve and the canonical guide is reachable at the configured release URL.

## Surprises & discoveries

- The application currently has no general Help/About menu; adding a single toolbar entry is more consistent and lower scope than introducing an otherwise empty Help menu.
- Debug `LogReader.App` builds do not produce the MCP executable because preserving the WPF/MCP dependency boundary is intentional. Missing-sidecar presentation is therefore a normal development scenario, not only an installation error.
- Portable artifact validation rejects unexpected root files, so bundling Markdown or HTML would require a packaging-contract change. The selected design avoids that expansion.

## Risks and mitigations

- Risk: the external GitHub guide URL changes or is unavailable. Mitigation: centralize the URL, keep essential offline explanations in the window, and verify the release URL before publishing.
- Risk: static client commands drift. Mitigation: do not copy Codex/Claude commands into application code; keep them in the dated canonical guide.
- Risk: users interpret readiness as a live MCP health check. Mitigation: label it as local setup/readiness and explicitly state that the UI does not start or connect to the sidecar.
- Risk: a new UI service expands already-long `MainViewModel` composition. Mitigation: use the smallest seam consistent with existing dialog services and isolate additions in a focused partial/record rather than refactoring unrelated composition.
- Risk: recursive dashboard counting disagrees with persisted catalog semantics. Mitigation: define the UI count as loaded `LogGroupKind.Dashboard` nodes and test nested trees; do not claim it is the MCP catalog revision or file count.
- Risk: toolbar crowding. Mitigation: use the existing toolbar button style and validate at constrained widths before considering a broader navigation redesign.
- Risk: browser launching can execute unsafe input. Mitigation: open only a compile-time HTTPS URL with `UseShellExecute`, never concatenate user input, and handle failure.
- Risk: clipboard or browser APIs make tests flaky. Mitigation: place them behind injected delegates/services and prohibit real external side effects in automated tests.

## Deferred work

- A live **Test server** action or embedded MCP client.
- Automatic Codex or Claude Code configuration.
- A dedicated hosted documentation site or bundled offline HTML documentation.
- A general Help/About menu containing broader WeezTail documentation.
- First-run MCP onboarding, telemetry, diagnostics export, or update notifications.
- Server-wide MCP `instructions` enhancements; those belong to the sidecar protocol surface and are independent of desktop help discoverability.

## Decision log

- 2026-08-18: Choose a hybrid surface: concise offline in-app help plus a browser link to authoritative Markdown.
- 2026-08-18: Use a single **MCP Server** toolbar entry rather than create a general Help menu with only one new destination.
- 2026-08-18: Include readiness information but define it as local setup state; do not launch or query the MCP process.
- 2026-08-18: Keep the WPF application free of `LogReader.Mcp` and MCP SDK references.
- 2026-08-18: Do not bundle Markdown/HTML because the existing portable root is intentionally strict and essential offline content fits in the modal window.
- 2026-08-18: Copy only the executable path from the app; keep client-specific commands in the canonical guide to reduce drift.

## Outcomes & retrospective

- Not yet implemented. At plan completion, record the observable UI outcome, changed files/components, exact validation results, remaining risks, and whether the hybrid documentation split proved maintainable.

## Handoff history

- None.
