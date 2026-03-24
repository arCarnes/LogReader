namespace LogReader.App.ViewModels;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.Core;
using LogReader.Core.Models;

public partial class ReplacementPatternViewModel : ObservableObject
{
    public ReplacementPatternViewModel()
    {
        OnNameChanged(_name);
        OnReplacePatternChanged(_replacePattern);
    }

    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _findPattern = string.Empty;

    [ObservableProperty]
    private string _replacePattern = string.Empty;

    [ObservableProperty]
    private string _nameError = string.Empty;

    [ObservableProperty]
    private string _replacePatternError = string.Empty;

    public bool HasErrors => !string.IsNullOrEmpty(NameError) || !string.IsNullOrEmpty(ReplacePatternError);

    public bool IsNameInvalid => !string.IsNullOrEmpty(NameError);

    public bool IsReplacePatternInvalid => !string.IsNullOrEmpty(ReplacePatternError);

    public string ValidationError => !string.IsNullOrEmpty(NameError) ? NameError : ReplacePatternError;

    partial void OnNameChanged(string value)
    {
        NameError = string.IsNullOrWhiteSpace(value)
            ? "Name is required."
            : string.Empty;
    }

    partial void OnReplacePatternChanged(string value)
    {
        ReplacePatternError = ReplacementTokenParser.Validate(value) ?? string.Empty;
    }

    partial void OnNameErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(IsNameInvalid));
        OnPropertyChanged(nameof(ValidationError));
    }

    partial void OnReplacePatternErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(IsReplacePatternInvalid));
        OnPropertyChanged(nameof(ValidationError));
    }

    public ReplacementPattern ToModel() => new()
    {
        Id = Id,
        Name = Name,
        FindPattern = FindPattern,
        ReplacePattern = ReplacePattern
    };

    public static ReplacementPatternViewModel FromModel(ReplacementPattern model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        FindPattern = model.FindPattern,
        ReplacePattern = model.ReplacePattern
    };
}
