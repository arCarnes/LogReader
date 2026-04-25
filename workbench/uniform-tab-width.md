# Uniform Tab Width

## Summary
Keep tab headers within a uniform width range so long file names ellipsize instead of expanding tabs.

## Implementation
- Add a maximum width to the tab item container matching the existing minimum width.
- Preserve the stretched header grid and right-aligned close button.
- Keep filename ellipsis behavior within the fixed tab bounds.

## Validation
- Add XAML assertions for tab maximum width and ellipsis behavior.
- Run `dotnet build LogReader.sln`.
- Run `dotnet test LogReader.sln`.
