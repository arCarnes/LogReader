namespace LogReader.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.Core;
using LogReader.Core.Models;

public partial class ReplacementPatternViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _findPattern = string.Empty;

    [ObservableProperty]
    private string _replacePattern = string.Empty;

    [ObservableProperty]
    private string _replacePatternError = string.Empty;

    public bool HasErrors => !string.IsNullOrEmpty(ReplacePatternError);

    partial void OnReplacePatternChanged(string value)
    {
        ReplacePatternError = ReplacementTokenParser.Validate(value) ?? string.Empty;
    }

    public ReplacementPattern ToModel() => new()
    {
        Name = Name,
        FindPattern = FindPattern,
        ReplacePattern = ReplacePattern
    };

    public static ReplacementPatternViewModel FromModel(ReplacementPattern model) => new()
    {
        Name = model.Name,
        FindPattern = model.FindPattern,
        ReplacePattern = model.ReplacePattern
    };
}
