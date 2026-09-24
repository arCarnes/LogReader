# Appearance Settings — Execution Plan

## Document control
Branch: `feature/settings-dashboard-font-dark-mode`. Owner: Codex. Created: 2026-09-24.

## Resume checkpoint
The command bar simplification is committed locally as `ca65665`; the branch is one commit ahead of origin and the working tree is clean. The next proposed work is a light-palette refresh. No palette implementation has started; review this plan before implementation.

## Purpose and observable outcome
Settings offers Dashboards font size and an app-wide Dark mode toggle. Saved choices apply to the open app and persist across restarts.

## Scope
Settings UI/model/persistence, dashboard pane typography, application-owned WPF theme resources and controls, including title bars, viewport scroll bars, command bar, relevant tests and documentation. The proposed follow-up adjusts light-mode neutral surfaces and borders across the main window and dialogs.

## Non-goals
Windows-owned picker theming, automatic Windows theme following, changing user-selected highlight colors.

## Definitions
Dashboard primary text: folder/dashboard names and name editor. Member text: file names. Secondary text: host, size, and row errors.

## Existing behavior and evidence
`AppSettings` persists through the version-one JSON envelope. `SettingsViewModel` builds a new model on save. `MainViewModel` reloads settings and calls `WpfLogAppearanceService.Apply`. Dashboard text sizes are fixed in `DashboardTreeView.xaml`; light palette resources live in `App.xaml` and are referenced across views.

## Decisions and invariants
- `DashboardFontSize` defaults to 12 with options 10–18. Primary text uses the selected size, member text one point less, secondary text two points less.
- `IsDarkMode` defaults to false and changes the entire application-owned UI on Save, not while editing the dialog.
- Missing JSON fields retain defaults; no schema bump. Import/export includes both fields.
- Preserve existing user-selected highlight colors.

## Open questions
None.

## Milestones / issue summary
1. Persist and expose new settings; tests cover load/save/import/export and defaults.
2. Apply dashboard font size at startup and on Save, with responsive row layout.
3. Apply complete light/dark palettes to owned windows and controls; inspect both modes.
4. Build, test, update docs, and commit.
5. Follow-up: apply dark/light mode to standard Windows title bars and both viewport scroll bars, then validate and commit.
6. Follow-up: apply dark/light mode to the toolbar overflow control beside Settings while preserving overflow commands, then validate and commit.
7. Repair: verify theme attachment on actual application window types and rendered chevron, fix any missing bindings, validate, and commit.
8. Simplify: remove the toolbar and chevron template; keep all commands available in a wrapping row, validate both themes and narrow widths, and commit.
9. Proposed: soften the light palette's large neutral surfaces, coordinate adjacent surfaces and borders, keep the blue emphasis colors stable, and inspect the full UI before implementation is accepted.

## Progress
- Branch created and repository/planning guidance inspected.
- Added persisted appearance settings, UI controls, dashboard font resources, and app-wide theme resources.
- Focused settings and WPF tests passed; rendered and inspected light/dark Settings and main windows.
- Full solution build passed with no warnings or errors. The final test run passed 941 app tests and 554 core tests.
- A light-mode viewport selection regression found by the first full run was repaired and the affected tests and full suite rerun successfully.
- Follow-up complete: standard Windows title bars and both viewport scroll bar orientations use the saved palette. Focused tests cover existing/new window bindings, live thumb colors, and scroll navigation.
- Toolbar overflow follow-up complete: replaced the default Windows-colored toolbar template with a palette-bound overflow button, popup, and grip. A focused WPF test verifies live colors and Settings in the overflow menu at narrow width.
- User report reproduced a title bar attachment failure on `MainWindow`: an implicit `Window` style did not set the attached properties on derived window types. Bound them directly on all seven application windows. The main-window test now checks attachment and chevron stroke, and a dark main-toolbar render was visually inspected.
- The user chose to remove overflow behavior and its custom chevron. The main command row now uses a standard `WrapPanel` and palette-bound surface and dividers.
- The wrapping-row WPF test passed at narrow and wide widths, including live dark/light surface updates. The full solution build passed without warnings; all 944 app and 554 core tests passed.
- Inspected the light brush definitions and their use in the main window, viewport, dashboard tree, search workspace, settings, and common controls. The proposed palette refresh below is planning only; no production colors have changed.

## Light palette refresh (proposed)
- State: planned; implementation awaits the user's request.
- Purpose: reduce the glare of the large near-white viewport and white controls while retaining the current blue emphasis and readable text.
- Current evidence: the viewport is `#FCFDFE`, controls are `#FFFFFF`, elevated surfaces are `#FBFCFD`, the window is `#F7F8FA`, and the command/dashboard surfaces are `#F4F6F8`. These light values cover most of the screen. The light title bar currently uses the Windows default rather than an app color.
- Direction: use a restrained, neutral cool-grey scale. Starting targets for visual review: window `#EBEEF1`; command/dashboard surface `#E5EAEE`; viewport chrome `#E6EAEE`; viewport content `#F1F3F5`; elevated cards `#F5F6F7`; input/button surfaces `#F6F7F8`. Bring branch rows, search panels, and headers into the same scale, with borders/dividers adjusted just enough to remain visible. These are candidate colors, not final values.
- Invariants: preserve `AppSelectedRowBrush`, `AppSelectedMemberBrush`, `AppViewportSelectionBrush`, focus and control hover/pressed blues, user-selected search highlight, and dark-mode values unless visual review exposes a specific problem. Keep the existing dark-text colors if contrast remains sufficient. Do not add a setting, dependency, or custom control template.
- Expected implementation areas: the light values in `App.xaml` and `WpfLogAppearanceService.ThemeBrushes` must match on startup and after Save. If the default white light title bar stands out against the revised window, set its light caption/text through the existing `WindowTitleBarTheme` DWM path, with the current fallback on unsupported Windows versions. Review any remaining app-owned fixed light colors before touching view markup.
- Tasks: (1) establish a small palette sample in the running main window; (2) tune the neutral brushes together across viewport, navigation, search, controls, and dialogs; (3) check title bar transition; (4) inspect normal, hover, selected, disabled, and warning states in both modes; (5) update focused color/resource tests and document final values.
- Acceptance criteria: light mode has no large white or near-white field; layer boundaries remain legible without heavy outlines; log text, muted text, and controls remain readable; blue selection and focus still look like the existing product; opening Settings and switching light/dark updates existing and newly created windows; dark mode remains visually unchanged.
- Focused validation: WPF tests for the refreshed resource values on startup and after live theme changes, including the main command bar, viewport, and Settings. Then `dotnet build LogReader\LogReader.sln --no-restore -m:1` and `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1`, plus visual inspection of the main window and Settings in both themes.
- Progress/evidence: palette usage audited; implementation and validation pending.

## Final validation and demonstration
- `dotnet build LogReader\LogReader.sln --no-restore -m:1`: passed, zero warnings/errors.
- `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1`: passed, 941 app and 554 core tests.
- Rendered and visually inspected Settings and main windows in light and dark modes. Confirmed dashboard font resources at 10 and 18 through WPF tests.
- Follow-up: `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors. `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 943 app and 554 core tests on rerun. The first full run had one unrelated collection-modified failure in `SearchPanelViewModelTests`; its isolated rerun and the full rerun passed.
- Toolbar overflow follow-up: `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors; `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 944 app and 554 core tests.
- Repair: focused `MainToolBarOverflow`, `SettingsWindow_UsesDarkControlSurfaces`, and `AppearanceService_UpdatesTitleBarForExistingAndNewWindows` tests passed. A dark `MainWindow` toolbar render showed the chevron and surrounding strip using the palette. `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors and `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 944 app and 554 core tests. The final main-window binding assertion passed on focused rerun.
- Simplification: `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors; focused `MainCommandBar_WrapsCommandsAndFollowsPalette` passed; `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 944 app and 554 core tests.

## Surprises & discoveries
Most palette references used `StaticResource`; they were changed to `DynamicResource` so open controls update. WPF's default ComboBox remained white in dark mode, so the app now supplies a theme-aware template.
The first full test run caught selected log lines changing from their original light blue; a dedicated viewport selection brush preserves that color.
The title bar is owned by Windows rather than WPF. Windows 11 DWM caption and text color attributes can follow the app setting independently of the system theme; unsupported systems need a graceful fallback.
WPF Track layout moved a minimum-sized custom thumb slightly short of the bottom. Removing its minimum size restored the existing scroll-position contract. DWM caption attributes could not be read back through `DwmGetWindowAttribute` in this environment; tests verify attached-property updates and the implementation sends the documented attributes.
The first title bar test manually attached the theme to a plain `Window`, so it missed the implicit-style lookup failure on real window subclasses. Tests now use `MainWindow` and `SettingsWindow` to cover the application path.

## Risks and mitigations
WPF default control chrome can retain light colors. Theme explicit control surfaces and inspect each app-owned window. Dashboard path shortening depends on font metrics; verify after size changes.

## Deferred work
Windows-owned pickers retain the operating-system theme by design.

## Decision log
2026-09-24: User selected a shared Appearance section, app-wide dark mode, and proportional dashboard row sizing.
2026-09-24: User requested title bar and viewport scroll bar coverage. Use DWM for standard window chrome and WPF templates scoped to the viewport.
2026-09-24: User requested the small control beside Settings in the top toolbar follow dark mode. It is WPF's overflow button; use a toolbar template with palette-bound button and popup while preserving overflow.
2026-09-24: User reported scroll bars dark but title bar and chevron still light. Verify actual window subclasses and rendered toolbar; use explicit attached properties on each application window rather than relying on an implicit base-type style.
2026-09-24: User prefers to avoid custom chevron work. Replace the `ToolBar` with a wrapping command row so all commands remain visible at narrow widths.
2026-09-24: User requested a less glaring light mode with related neutrals adjusted but blue highlights largely preserved. Propose a coordinated light-only neutral palette update, using the existing brush resources and DWM title bar path.

## Outcomes & retrospective
Settings now persists dashboard font size and dark mode, applies both when saved, and keeps existing light-mode viewport selection color. Runtime theme changes required dynamic brush references and theme-aware templates for WPF text and combo inputs. The follow-up extends the same live setting to title bars and viewport scroll bars. Build and the full test rerun pass; the first full run exposed an unrelated intermittent dashboard member refresh test failure.
The toolbar overflow control beside Settings now follows the same live palette and retains its overflow commands and draggable grip. Its focused test and the full solution validation pass.
The title bar correction binds the theme directly on all seven application windows, covering derived window types missed by the first test. The main-window test confirms live title bar state and chevron stroke color; the rendered dark toolbar confirms the visible chrome. The full build and suite pass.
The command bar now wraps its seven actions across rows when narrow. Removing `ToolBar` also removes the overflow chevron, popup, grip, and their custom template. The row surface and dividers follow the app palette, and full validation passes.

## Handoff history
None.
