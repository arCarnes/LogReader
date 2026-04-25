# Align Tab Close Button

## Summary
Keep each tab's close button anchored to the right edge of the tab header instead of immediately after short file names.

## Implementation
- Stretch the tab item content horizontally.
- Replace the tab header's horizontal `StackPanel` with a two-column `Grid`.
- Put the filename in the flexible column and the close button in the auto-sized right column.
- Preserve existing pin marker, context menu, close command, and virtualization behavior.

## Validation
- Add XAML layout assertions for the stretched tab header.
- Run `dotnet build LogReader.sln`.
- Run `dotnet test LogReader.sln`.
