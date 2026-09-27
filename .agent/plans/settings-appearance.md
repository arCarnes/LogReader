# Appearance Settings — Execution Plan

## Document control
Branch: `feature/settings-dashboard-font-dark-mode`. Owner: Codex. Created: 2026-09-24.

## Resume checkpoint
Milestone 13 is implemented and validated on the current branch. Easy Reading's empty and loaded viewport backgrounds now use `#E6EAEE`, another approximate 10% blend toward white from `#E3E8EC`. Default, Dark, dashboards, and results retain their prior values. The solution build and full test suite pass using isolated `bin/ViewportLight2Validation/` output. Do not push unless requested.

## Purpose and observable outcome
Settings offers Dashboards font size and an app-wide Default, Easy Reading, or Dark theme. Saved choices apply to the open app and persist across restarts.

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
- Theme defaults to the original light palette and changes the entire application-owned UI on Save, not while editing the dialog. Legacy `IsDarkMode` remains for older settings files.
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
9. Implement the approved softer light palette across all three content panes, surrounding surfaces, and standard title bars; preserve blue accents and dark mode; validate and commit.
10. Replace the dark toggle with Default, Easy Reading, and Dark choices; retain backward-compatible settings, apply all three palettes and title bars, validate, and commit.
11. Replace the Theme dropdown with a native horizontal radio-button group for Default, Easy Reading, and Dark; validate and commit.
12. Lighten the Easy Reading viewport by about 10% toward white, including its empty state, without changing Default, Dark, dashboards, or results; validate and commit.
13. Lighten the Easy Reading viewport another 10% toward white, including empty and loaded states; preserve the other surfaces, validate, and commit.

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
- Inspected the original light brush definitions and their use in the main window, viewport, dashboard tree, search workspace, settings, and common controls before changing the palette.
- Confirmed that dashboard and results content containers are transparent and inherit the pale window background, while the viewport uses its own near-white brush. Revised the proposal to cover all three explicitly.
- Implementation discovery: application windows are derived `Window` types, and a prior title-bar bug showed that the implicit base `Window` style does not reliably attach properties to them. Bind the background and foreground resources explicitly on all seven application windows so empty canvas and dialogs receive the palette.
- First focused build could not copy referenced DLLs into the app's normal `bin/Debug` folder because another process has them open. Validate in an isolated output folder without closing the running app, then run the required build/tests there.
- Palette resources, explicit backgrounds for all seven windows, dashboard/results content surfaces, and light DWM caption colors are implemented. Focused WPF tests check light/dark/light updates and unchanged blue/dark values.
- Rendered the actual WPF main-window content in both themes and inspected the three pane backgrounds, chrome, controls, and text. The OS-owned title bar is outside the WPF render; its DWM constants were reviewed, while existing tests cover theme attachment on real window types.
- Three appearance modes implemented. Settings and JSON persistence handle Default, Easy Reading, and Dark, including the legacy dark flag. All 34 palette roles were audited against historical colors, six WPF previews inspected, and the full solution build and tests passed.
- Theme selection now uses three radio buttons, following the existing Search workspace control pattern. WPF tests verify selection in both directions, and Settings was rendered and inspected in all three modes.
- Easy Reading's log list and empty viewport now share the slightly lighter `#E3E8EC` background. The Default/Dark viewport and other Easy Reading surfaces remain unchanged. Focused WPF tests, three main-window renders, the solution build, and the full suite pass.
- The second viewport adjustment sets both Easy Reading viewport brushes to `#E6EAEE`. Focused tests, the rendered main window, the solution build, and the full suite pass; other palette values are unchanged.

## Light palette refresh (proposed)
- State: implemented, validated, and committed on 2026-09-25.
- Purpose: reduce the glare of the large near-white viewport and white controls while retaining the current blue emphasis and readable text.
- Before implementation: the viewport was `#FCFDFE`. `DashboardTreeView` and `SearchWorkspaceView` used transparent list/root backgrounds, so their content areas showed the near-white window background (`#F7F8FA`). Controls were `#FFFFFF`, elevated surfaces were `#FBFCFD`, and command/dashboard headers were `#F4F6F8`. The light title bar used the Windows default rather than an app color.
- Direction: use the locked flat values in `.agent/design/softer-light-mode-palette.md`: viewport, results, and window `#E0E5EA`; dashboard content `#D9E0E6`; chrome `#D8DFE5`; secondary rows `#E8EDF1`; raised controls `#F1F3F6`; borders `#BCC8D2`; dividers `#CBD4DD`.
- Invariants: preserve `AppSelectedRowBrush`, `AppSelectedMemberBrush`, `AppViewportSelectionBrush`, focus and control hover/pressed blues, user-selected search highlight, and dark-mode values unless visual review exposes a specific problem. Keep the existing dark-text colors if contrast remains sufficient. Do not add a setting, dependency, or custom control template.
- Expected implementation areas: update matching light resources in `App.xaml` and `WpfLogAppearanceService.ThemeBrushes`. Bind background/foreground resources explicitly on all application windows. Add a dashboard-content brush (`#D9E0E6` light, `#151A21` dark) to paint its entire pane; paint the results pane and list with `AppBackgroundBrush`, which already has the correct dark value; retain `AppViewportContentBrush` for open logs. Set the light DWM caption/text to `#D8DFE5`/`#1F2937`, preserving the dark constants and unsupported-system fallback.
- Tasks: (1) update palette resources and pane backgrounds; (2) set the standard light caption color; (3) verify normal, hover, selected, disabled, and warning states in both modes; (4) update focused color/resource tests; (5) build, test, inspect the running UI, and commit.
- Acceptance criteria: the viewport, dashboard list, and search results all show a softer coordinated grey content background in light mode, including empty areas; their layer boundaries remain legible without heavy outlines; log text, muted text, and controls remain readable; blue selection and focus still look like the existing product; opening Settings and switching light/dark updates existing and newly created windows; dark mode remains visually unchanged.
- Focused validation: WPF tests for the refreshed resource values on startup and after live theme changes, including all three pane backgrounds, the main command bar, and Settings. Then `dotnet build LogReader\LogReader.sln --no-restore -m:1` and `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1`, plus visual inspection of the main window and Settings in both themes.
- Progress/evidence: palette usage audited and final colors locked in `de78b9d`. Exact light values are in both resource definitions; dashboard/results paint their full areas; dark values and blue accents are unchanged. Focused and full tests pass; light/dark WPF renders were inspected.

## Three appearance modes
- State: implemented and validated on 2026-09-26.
- Purpose: offer the prior light palette as Default while keeping the approved softer grey palette available as Easy Reading and retaining Dark.
- Settings contract: add nullable `AppTheme` to `AppSettings`; an explicit valid theme wins over legacy `IsDarkMode`. With no theme, `IsDarkMode=true` maps to Dark and false/missing maps to Default. Save and export write both fields consistently without changing the JSON schema version.
- UI: replace the Dark mode checkbox with a three-option Theme selector. The choice applies on Save through the existing settings reload path.
- Palette: recover original light values from the parent of `c9ced6b`, use `#F7F8FA` for the now-explicit dashboard background, retain current grey values for Easy Reading and current dark values for Dark. Initialize `App.xaml` with Default. Keep blue accents and user-selected search highlights unchanged.
- Title bars: bind an app theme resource on all seven derived windows. Default restores Windows caption/text defaults; Easy Reading keeps its grey caption; Dark keeps its current caption. Existing and new windows update with saved settings.
- Acceptance: old settings files open in the corresponding Default/Dark choice; new choices round-trip through save/import/export; the main panes, controls, scroll bars, title bars, and Settings follow Default → Easy Reading → Dark → Default; blue accents are unchanged.
- Validation: focused settings, repository, layout, and WPF tests; solution build and tests; visual inspection of three modes. Use isolated output if necessary.
- Progress/evidence: the Settings selector, model compatibility, three-column palette, startup resources, and seven title-bar bindings are implemented. A static audit matched all 34 Default brush values against the pre-grey commit, except the intentionally new dashboard brush (`#F7F8FA`); Easy Reading and Dark match their prior values. Rendered the main window and Settings in all three modes. The first render exposed the custom ComboBox showing the option record instead of its label; `ThemeOption.ToString()` now supplies the label, and a WPF assertion covers it. The main and Settings renders were inspected again. The OS title bar is outside the WPF bitmap; the attached theme state is tested and DWM color selection reviewed.

## Final validation and demonstration
- `dotnet build LogReader\LogReader.sln --no-restore -m:1`: passed, zero warnings/errors.
- `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1`: passed, 941 app and 554 core tests.
- Rendered and visually inspected Settings and main windows in light and dark modes. Confirmed dashboard font resources at 10 and 18 through WPF tests.
- Follow-up: `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors. `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 943 app and 554 core tests on rerun. The first full run had one unrelated collection-modified failure in `SearchPanelViewModelTests`; its isolated rerun and the full rerun passed.
- Toolbar overflow follow-up: `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors; `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 944 app and 554 core tests.
- Repair: focused `MainToolBarOverflow`, `SettingsWindow_UsesDarkControlSurfaces`, and `AppearanceService_UpdatesTitleBarForExistingAndNewWindows` tests passed. A dark `MainWindow` toolbar render showed the chevron and surrounding strip using the palette. `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors and `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 944 app and 554 core tests. The final main-window binding assertion passed on focused rerun.
- Simplification: `dotnet build LogReader\LogReader.sln --no-restore -m:1` passed with zero warnings/errors; focused `MainCommandBar_WrapsCommandsAndFollowsPalette` passed; `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1` passed 944 app and 554 core tests.
- Light palette: `dotnet build LogReader\LogReader.sln --no-restore -m:1 -p:OutputPath=bin/PaletteValidation/` passed with zero warnings/errors. Focused `WpfTestHostTests` passed 10/10. `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1 -p:OutputPath=bin/PaletteValidation/` passed 945 app and 554 core tests. The first normal-output build failed on DLL file locks held by the running app; isolated-output build repaired validation without terminating it.
- Three modes: `dotnet build LogReader\LogReader.sln --no-restore -m:1 -p:OutputPath=bin/ThemeModeValidation/` passed with zero warnings/errors. Focused settings, repository, layout, and WPF tests passed. `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1 -p:OutputPath=bin/ThemeModeValidation/` passed 949 app and 560 core tests. All 34 palette roles passed a comparison against the original light and current grey/dark values; six WPF main/Settings renderings were visually inspected, and the corrected selector label was rerendered.
- Radio selection: `dotnet build LogReader\LogReader.sln --no-restore -m:1 -p:OutputPath=bin/ThemeRadioValidation/` passed with zero warnings/errors. Focused Settings tests passed 38/38. Settings previews in Default, Easy Reading, and Dark were inspected. First full run hit the previously observed intermittent collection-modified failure in `SearchPanelViewModelTests`; that test passed alone and the full rerun passed 949 app and 560 core tests.
- Lighter viewport: `dotnet build LogReader\LogReader.sln --no-restore -m:1 -p:OutputPath=bin/ViewportLightValidation/` passed with zero warnings/errors. Focused viewport and palette tests passed 2/2. `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1 -p:OutputPath=bin/ViewportLightValidation/` passed 949 app and 560 core tests. Main-window previews in all three modes were inspected; Easy Reading shows the intended subtle viewport separation.
- Second viewport adjustment: `dotnet build LogReader\LogReader.sln --no-restore -m:1 -p:OutputPath=bin/ViewportLight2Validation/` passed with zero warnings/errors. Focused viewport and palette tests passed 2/2. `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1 -p:OutputPath=bin/ViewportLight2Validation/` passed 949 app and 560 core tests. Default and Easy Reading main-window previews were inspected; the viewport is slightly lighter again.

## Surprises & discoveries
Most palette references used `StaticResource`; they were changed to `DynamicResource` so open controls update. WPF's default ComboBox remained white in dark mode, so the app now supplies a theme-aware template.
The first full test run caught selected log lines changing from their original light blue; a dedicated viewport selection brush preserves that color.
The title bar is owned by Windows rather than WPF. Windows 11 DWM caption and text color attributes can follow the app setting independently of the system theme; unsupported systems need a graceful fallback.
WPF Track layout moved a minimum-sized custom thumb slightly short of the bottom. Removing its minimum size restored the existing scroll-position contract. DWM caption attributes could not be read back through `DwmGetWindowAttribute` in this environment; tests verify attached-property updates and the implementation sends the documented attributes.
The first title bar test manually attached the theme to a plain `Window`, so it missed the implicit-style lookup failure on real window subclasses. Tests now use `MainWindow` and `SettingsWindow` to cover the application path.
The custom ComboBox template rendered `ThemeOption` using its record string despite `DisplayMemberPath`; the saved selection worked but the displayed text was wrong. Overriding `ToString()` to return the label fixed it, with a focused assertion and visual check.
The first viewport test attempted to inspect a log `ListBox` while the main window had no tab, so that control had not been created. The empty-window test now checks its viewport canvas, and a loaded-tab test checks the list background.

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
2026-09-24: User clarified that the viewport, dashboards pane, and search results pane all appear bright white. Treat all three content backgrounds as first-class palette targets; the latter two currently show the window background through transparent containers.
2026-09-25: User approved the darker mockup and requested implementation. Use the exact color target, explicitly paint the dashboard/results content areas, and set the light caption color through the existing DWM helper.
2026-09-25: Explicitly bind window background/foreground on the seven derived window types; the previously documented implicit-style failure also makes the empty main canvas and dialog surfaces unreliable.
2026-09-26: User requested three modes. Make the original light palette Default and the approved grey palette Easy Reading; retain Dark. Existing light users map to Default, existing dark users to Dark. Preserve the legacy flag while making an explicit new theme authoritative.
2026-09-27: User requested a visible horizontal theme selector, following a segmented-choice reference but using basic WPF controls. Use the app's native radio-button pattern and keep the existing save behavior.
2026-09-27: User requested a slightly lighter Easy Reading viewport. Blend `#E0E5EA` 10% toward white to `#E3E8EC`. The empty viewport currently inherits the window background, so add a dedicated viewport canvas brush with its prior Default/Dark colors and the new Easy Reading color; update the log list brush's Easy Reading value as well. Leave other surfaces unchanged.
2026-09-27: User requested another 10% lighter viewport. Apply the same toward-white blend to the current Easy Reading color, `#E3E8EC` to `#E6EAEE`, for both empty and loaded viewport brushes. Keep other mode/surface values unchanged.

## Outcomes & retrospective
Settings now persists dashboard font size and dark mode, applies both when saved, and keeps existing light-mode viewport selection color. Runtime theme changes required dynamic brush references and theme-aware templates for WPF text and combo inputs. The follow-up extends the same live setting to title bars and viewport scroll bars. Build and the full test rerun pass; the first full run exposed an unrelated intermittent dashboard member refresh test failure.
The toolbar overflow control beside Settings now follows the same live palette and retains its overflow commands and draggable grip. Its focused test and the full solution validation pass.
The title bar correction binds the theme directly on all seven application windows, covering derived window types missed by the first test. The main-window test confirms live title bar state and chevron stroke color; the rendered dark toolbar confirms the visible chrome. The full build and suite pass.
The command bar now wraps its seven actions across rows when narrow. Removing `ToolBar` also removes the overflow chevron, popup, grip, and their custom template. The row surface and dividers follow the app palette, and full validation passes.
The light palette now uses the approved cool-grey values across the three content panes and surrounding chrome. Explicit window backgrounds cover blank canvas and dialogs; blue accents and dark brush values remain unchanged. WPF renders and full tests confirm the application-owned UI. The standard light title bar receives the approved DWM caption/text colors; unsupported systems retain the Windows fallback.
The three-mode selector now persists Default, Easy Reading, and Dark while preserving older `IsDarkMode` settings. Default recovers the original near-white palette and Windows caption default, Easy Reading keeps the approved grey palette, and Dark keeps its prior values. The app applies each mode live after Save; palette audit, WPF renders, focused tests, and the full suite pass.
The Theme selector now displays all three choices on one row as native radio buttons. The selected option follows loaded settings and changes the saved theme through the existing view model. No palette or persistence changes were needed; the final full suite and three rendered Settings views pass visual review.
The Easy Reading viewport is now about 10% lighter while the dashboard and results remain at their approved grey values. Separate canvas and log-content resources cover both empty and loaded states, and live theme switching, render review, and the full suite confirm the change.
The follow-up lightens the Easy Reading viewport another 10% from its previous value, to `#E6EAEE`. Empty and loaded states match; visual review and the full suite pass without changes to the other palettes or panes.

## Handoff history
None.
