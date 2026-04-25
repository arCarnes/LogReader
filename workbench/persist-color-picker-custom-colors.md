# Persist Color Picker Custom Colors

## Summary
Persist the WinForms color dialog custom palette in existing app settings so highlight-rule color pickers share the same custom colors across Settings dialog opens and app restarts.

## Implementation
- Add `AppSettings.ColorPickerCustomColors` as a `List<string>` of normalized `#RRGGBB` values.
- Load and save the palette through `SettingsViewModel`.
- Add a WinForms custom-color conversion helper that maps `#RRGGBB` values to `ColorDialog.CustomColors` integers and back, ignoring invalid/blank values and capping the palette at 16 entries.
- Initialize every Settings color dialog with the shared palette and write accepted dialog palettes back to the view model.

## Validation
- Add repository, view-model, and conversion helper tests.
- Run `dotnet build LogReader.sln`.
- Run `dotnet test LogReader.sln`.
