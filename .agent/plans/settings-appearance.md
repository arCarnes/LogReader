# Appearance Settings — Execution Plan

## Document control
Branch: `feature/settings-dashboard-font-dark-mode`. Owner: Codex. Created: 2026-09-24.

## Resume checkpoint
Implementation and validation are complete. Review staged changes and create the required local commit.

## Purpose and observable outcome
Settings offers Dashboards font size and an app-wide Dark mode toggle. Saved choices apply to the open app and persist across restarts.

## Scope
Settings UI/model/persistence, dashboard pane typography, application-owned WPF theme resources and controls, relevant tests and documentation.

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

## Progress
- Branch created and repository/planning guidance inspected.
- Added persisted appearance settings, UI controls, dashboard font resources, and app-wide theme resources.
- Focused settings and WPF tests passed; rendered and inspected light/dark Settings and main windows.
- Full solution build passed with no warnings or errors. The final test run passed 941 app tests and 554 core tests.
- A light-mode viewport selection regression found by the first full run was repaired and the affected tests and full suite rerun successfully.

## Final validation and demonstration
- `dotnet build LogReader\LogReader.sln --no-restore -m:1`: passed, zero warnings/errors.
- `dotnet test LogReader\LogReader.sln --no-build --no-restore -m:1`: passed, 941 app and 554 core tests.
- Rendered and visually inspected Settings and main windows in light and dark modes. Confirmed dashboard font resources at 10 and 18 through WPF tests.

## Surprises & discoveries
Most palette references used `StaticResource`; they were changed to `DynamicResource` so open controls update. WPF's default ComboBox remained white in dark mode, so the app now supplies a theme-aware template.
The first full test run caught selected log lines changing from their original light blue; a dedicated viewport selection brush preserves that color.

## Risks and mitigations
WPF default control chrome can retain light colors. Theme explicit control surfaces and inspect each app-owned window. Dashboard path shortening depends on font metrics; verify after size changes.

## Deferred work
Windows-owned pickers retain the operating-system theme by design.

## Decision log
2026-09-24: User selected a shared Appearance section, app-wide dark mode, and proportional dashboard row sizing.

## Outcomes & retrospective
Settings now persists dashboard font size and dark mode, applies both when saved, and keeps existing light-mode viewport selection color. Runtime theme changes required dynamic brush references and theme-aware templates for WPF text and combo inputs. The full test suite exposed and helped repair the viewport color regression before completion.

## Handoff history
None.
