# Stabilize Shared Color Picker Custom Colors

## Summary
Merge accepted color dialog palettes into the shared Settings custom colors and add the accepted selected color so custom colors carry across highlight rows.

## Implementation
- Add merge behavior to `ColorDialogCustomColors`.
- Preserve existing colors first, then dialog custom colors, then the accepted selected color.
- Deduplicate case-insensitively, ignore invalid entries, and cap at 16 colors.
- Keep cancel behavior unchanged.

## Validation
- Add merge helper tests.
- Run `dotnet build LogReader.sln`.
- Run `dotnet test LogReader.sln`.
