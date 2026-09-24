# Appearance Settings — Execution Plan

## Document control
Branch: `feature/settings-dashboard-font-dark-mode`. Owner: Codex. Created: 2026-09-24.

## Resume checkpoint
The title bar and toolbar overflow follow-ups were committed locally. The user-reported title bar issue is repaired and validated on real app windows; create a local repair commit. These follow-ups have not been requested for push.

## Purpose and observable outcome
Settings offers Dashboards font size and an app-wide Dark mode toggle. Saved choices apply to the open app and persist across restarts.

## Scope
Settings UI/model/persistence, dashboard pane typography, application-owned WPF theme resources and controls, including title bars, viewport scroll bars, and toolbar overflow, relevant tests and documentation.

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

## Progress
- Branch created and repository/planning guidance inspected.
- Added persisted appearance settings, UI controls, dashboard font resources, and app-wide theme resources.
- Focused settings and WPF tests passed; rendered and inspected light/dark Settings and main windows.
- Full solution build passed with no warnings or errors. The final test run passed 941 app tests and 554 core tests.
- A light-mode viewport selection regression found by the first full run was repaired and the affected tests and full suite rerun successfully.
- Follow-up complete: standard Windows title bars and both viewport scroll bar orientations use the saved palette. Focused tests cover existing/new window bindings, live thumb colors, and scroll navigation.
- Toolbar overflow follow-up complete: replaced the default Windows-colored toolbar template with a palette-bound overflow button, popup, and grip. A focused WPF test verifies live colors and Settings in the overflow menu at narrow width.
- User report reproduced a title bar attachment failure on `MainWindow`: an implicit `Window` style did not set the attached properties on derived window types. Bound them directly on all seven application windows. The main-window test now checks attachment and chevron stroke, and a dark main-toolbar render was visually inspected.

## Final validation and demonstration
- `dotnet build LogReader\LogReader.sln --no-restore -m:1`: passed, zero warnings/errors.
- `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1`: passed, 941 app and 554 core tests.
- Rendered and visually inspected Settings and main windows in light and dark modes. Confirmed dashboard font resources at 10 and 18 through WPF tests.
- Follow-up: `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors. `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 943 app and 554 core tests on rerun. The first full run had one unrelated collection-modified failure in `SearchPanelViewModelTests`; its isolated rerun and the full rerun passed.
- Toolbar overflow follow-up: `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors; `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 944 app and 554 core tests.
- Repair: focused `MainToolBarOverflow`, `SettingsWindow_UsesDarkControlSurfaces`, and `AppearanceService_UpdatesTitleBarForExistingAndNewWindows` tests passed. A dark `MainWindow` toolbar render showed the chevron and surrounding strip using the palette. `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors and `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 944 app and 554 core tests. The final main-window binding assertion passed on focused rerun.

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

## Outcomes & retrospective
Settings now persists dashboard font size and dark mode, applies both when saved, and keeps existing light-mode viewport selection color. Runtime theme changes required dynamic brush references and theme-aware templates for WPF text and combo inputs. The follow-up extends the same live setting to title bars and viewport scroll bars. Build and the full test rerun pass; the first full run exposed an unrelated intermittent dashboard member refresh test failure.
The toolbar overflow control beside Settings now follows the same live palette and retains its overflow commands and draggable grip. Its focused test and the full solution validation pass.
The title bar correction binds the theme directly on all seven application windows, covering derived window types missed by the first test. The main-window test confirms live title bar state and chevron stroke color; the rendered dark toolbar confirms the visible chrome. The full build and suite pass.

## Handoff history
None.
