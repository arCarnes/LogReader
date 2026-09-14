namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core;
using LogReader.Core.Models;

public partial class FieldProfilesViewModel : ObservableObject
{
    public const int MaximumSampleCharacters = 65_536;
    public ObservableCollection<FieldProfileEditorViewModel> Profiles { get; } = new();
    public ObservableCollection<FieldPreviewRow> PreviewRows { get; } = new();
    [ObservableProperty] private FieldProfileEditorViewModel? _selectedProfile;
    [ObservableProperty] private string _sampleText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    public FieldProfilesViewModel(IEnumerable<StructuredFieldProfile> profiles)
    {
        foreach (var profile in profiles) Profiles.Add(new(profile));
        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new FieldProfileEditorViewModel(new() { Name = "New profile" });
        profile.Fields.Add(new());
        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    [RelayCommand]
    private void RemoveProfile()
    {
        if (SelectedProfile != null) Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void AddField() => SelectedProfile?.Fields.Add(new());

    [RelayCommand]
    private void RemoveField(FieldDefinitionEditorViewModel? field)
    {
        if (field != null) SelectedProfile?.Fields.Remove(field);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PreviewAsync(CancellationToken ct)
    {
        PreviewRows.Clear();
        try
        {
            if (SelectedProfile == null) throw new ArgumentException("Select a profile first.");
            if (SampleText.Length > MaximumSampleCharacters) throw new ArgumentException("Preview supports at most 65,536 sample characters.");
            var extractor = StructuredFieldExtractor.Compile(SelectedProfile.ToModel());
            var sample = SampleText;
            var rows = await Task.Run(() =>
            {
                var output = new List<FieldPreviewRow>();
                using var reader = new System.IO.StringReader(sample);
                while (output.Count < 100 && reader.ReadLine() is { } line)
                {
                    ct.ThrowIfCancellationRequested();
                    var values = StructuredFieldOutput.Retain(extractor.Extract(line, ct), 8192);
                    output.Add(new(output.Count + 1, FormatFields(values)));
                }
                return output;
            }, ct);
            foreach (var row in rows) PreviewRows.Add(row);
            StatusText = $"Previewed {rows.Count} line(s), maximum 100. Samples are not saved. Rerun preview after editing rules.";
        }
        catch (OperationCanceledException) { StatusText = "Preview cancelled."; }
        catch (Exception ex) when (ex is ArgumentException or TimeoutException) { StatusText = ex.Message; }
    }

    public bool TryGetProfiles(out List<StructuredFieldProfile> profiles)
    {
        profiles = Profiles.Select(profile => profile.ToModel()).ToList();
        try { StructuredFieldExtractor.ValidateProfiles(profiles); return true; }
        catch (ArgumentException ex) { StatusText = ex.Message; return false; }
    }

    internal static string FormatFields(IReadOnlyDictionary<string, StructuredFieldValue> values)
        => string.Join(Environment.NewLine, values.OrderBy(field => field.Key).Select(field =>
            $"{field.Key}: " + (field.Value.State switch
            {
                StructuredFieldState.Missing => "(missing)",
                StructuredFieldState.Invalid => "(invalid number)",
                _ => field.Value.Text ?? field.Value.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            }) + (field.Value.IsTruncated ? " [truncated]" : string.Empty)));
}

public sealed record FieldPreviewRow(int LineNumber, string FieldsText);

public partial class FieldProfileEditorViewModel : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _name;
    public ObservableCollection<FieldDefinitionEditorViewModel> Fields { get; } = new();
    public FieldProfileEditorViewModel(StructuredFieldProfile profile)
    {
        Id = profile.Id;
        _name = profile.Name;
        foreach (var field in profile.Fields) Fields.Add(new(field));
    }
    public StructuredFieldProfile ToModel() => new() { Id = Id, Name = Name, Fields = Fields.Select(field => field.ToModel()).ToList() };
}

public partial class FieldDefinitionEditorViewModel : ObservableObject
{
    public static IReadOnlyList<StructuredFieldType> Types { get; } = Enum.GetValues<StructuredFieldType>();
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _pattern = string.Empty;
    [ObservableProperty] private StructuredFieldType _type;
    [ObservableProperty] private bool _caseSensitive;
    public FieldDefinitionEditorViewModel() { }
    public FieldDefinitionEditorViewModel(StructuredFieldDefinition field)
    {
        _name = field.Name; _pattern = field.Pattern; _type = field.Type; _caseSensitive = field.CaseSensitive;
    }
    public StructuredFieldDefinition ToModel() => new() { Name = Name, Pattern = Pattern, Type = Type, CaseSensitive = CaseSensitive };
}
