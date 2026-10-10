namespace LogReader.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.Core.Models;

public partial class HighlightRuleViewModel : ObservableObject
{
    [ObservableProperty]
    private string _pattern = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private string _color = "#FFFFFF";

    [ObservableProperty]
    private bool _isBackgroundColorEnabled = true;

    [ObservableProperty]
    private string _textColor = "#000000";

    [ObservableProperty]
    private bool _isTextColorEnabled;

    [ObservableProperty]
    private bool _isEnabled = true;

    public LineHighlightRule ToModel() => new()
    {
        Pattern = Pattern,
        IsRegex = IsRegex,
        CaseSensitive = CaseSensitive,
        Color = Color,
        IsBackgroundColorEnabled = IsBackgroundColorEnabled,
        TextColor = TextColor,
        IsTextColorEnabled = IsTextColorEnabled,
        IsEnabled = IsEnabled
    };
}
