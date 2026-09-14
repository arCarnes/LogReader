namespace LogReader.Tests;

using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core.Models;

public class FieldProfilesViewModelTests
{
    internal static StructuredFieldProfile Example() => new()
    {
        Id = "payments", Name = "Payments",
        Fields =
        [
            new() { Name = "level", Pattern = "(?<level>ERROR|INFO)" },
            new() { Name = "duration_ms", Pattern = @"duration=(?<duration_ms>\S+)", Type = StructuredFieldType.Number }
        ]
    };

    [Fact]
    public async Task PreviewUsesSharedExtractorAndDoesNotMutateSavedProfile()
    {
        var source = Example();
        var editor = new FieldProfilesViewModel([source]) { SampleText = "ERROR duration=842\nINFO duration=bad\nother" };
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(3, editor.PreviewRows.Count);
        Assert.Contains("duration_ms: 842", editor.PreviewRows[0].FieldsText);
        Assert.Contains("invalid number", editor.PreviewRows[1].FieldsText);
        Assert.Contains("missing", editor.PreviewRows[2].FieldsText);
        editor.SelectedProfile!.Name = "Edited";
        Assert.Equal("Payments", source.Name);
        Assert.True(editor.TryGetProfiles(out var saved));
        Assert.Equal("Edited", saved[0].Name);
        Assert.DoesNotContain("842", System.Text.Json.JsonSerializer.Serialize(saved));
    }

    [Fact]
    public async Task PreviewIsBoundedAndInvalidDefinitionsCannotBeAccepted()
    {
        var editor = new FieldProfilesViewModel([Example()]) { SampleText = string.Join('\n', Enumerable.Repeat("ERROR", 101)) };
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(100, editor.PreviewRows.Count);
        editor.SelectedProfile!.Fields[0].Name = "raw";
        Assert.False(editor.TryGetProfiles(out _));
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Empty(editor.PreviewRows);
        Assert.Contains("reserved", editor.StatusText);
    }

    [Fact]
    public void ProfileEditorWindowLoadsAndLaysOut()
    {
        WpfTestHost.Run(() =>
        {
            var window = new FieldProfilesWindow { DataContext = new FieldProfilesViewModel([Example()]) };
            window.Measure(new System.Windows.Size(980, 760));
            window.Arrange(new System.Windows.Rect(0, 0, 980, 760));
            window.UpdateLayout();
            Assert.NotNull(window.FindName("FieldsGrid"));
            window.Close();
        });
    }
}
