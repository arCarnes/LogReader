namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class SettingsViewModel : ObservableObject
{
    private const string DefaultLogFont = "Consolas";

    public sealed class EncodingOptionItem
    {
        public FileEncoding? Value { get; init; }
        public string Label { get; init; } = string.Empty;
    }

    public static IReadOnlyList<EncodingOptionItem> DefaultEncodingOptions { get; } = new[]
    {
        new EncodingOptionItem { Value = FileEncoding.Utf8, Label = "UTF-8" },
        new EncodingOptionItem { Value = FileEncoding.Utf8Bom, Label = "UTF-8 (BOM)" },
        new EncodingOptionItem { Value = FileEncoding.Ansi, Label = "ANSI (Windows-1252)" },
        new EncodingOptionItem { Value = FileEncoding.Utf16, Label = "UTF-16 LE" },
        new EncodingOptionItem { Value = FileEncoding.Utf16Be, Label = "UTF-16 BE" }
    };

    public static IReadOnlyList<EncodingOptionItem> FallbackEncodingOptions { get; } = new[]
    {
        new EncodingOptionItem { Value = null, Label = "(None)" },
        new EncodingOptionItem { Value = FileEncoding.Utf8, Label = "UTF-8" },
        new EncodingOptionItem { Value = FileEncoding.Utf8Bom, Label = "UTF-8 (BOM)" },
        new EncodingOptionItem { Value = FileEncoding.Ansi, Label = "ANSI (Windows-1252)" },
        new EncodingOptionItem { Value = FileEncoding.Utf16, Label = "UTF-16 LE" },
        new EncodingOptionItem { Value = FileEncoding.Utf16Be, Label = "UTF-16 BE" }
    };

    public static IReadOnlyList<string> LogFontOptions { get; } = new[]
    {
        "Consolas",
        "Cascadia Mono",
        "Cascadia Code",
        "Lucida Console",
        "Courier New"
    };

    private readonly ISettingsRepository _settingsRepo;
    private AppSettings _settings = new();

    [ObservableProperty]
    private string? _defaultOpenDirectory;

    [ObservableProperty]
    private bool _globalAutoTailEnabled = true;

    [ObservableProperty]
    private bool _enableTabOverflowDropdown = true;

    [ObservableProperty]
    private FileEncoding _defaultFileEncoding = FileEncoding.Utf8;

    [ObservableProperty]
    private FileEncoding? _fallbackEncoding1;

    [ObservableProperty]
    private FileEncoding? _fallbackEncoding2;

    [ObservableProperty]
    private FileEncoding? _fallbackEncoding3;

    [ObservableProperty]
    private string _logFontFamily = DefaultLogFont;

    public ObservableCollection<HighlightRuleViewModel> HighlightRules { get; } = new();

    public SettingsViewModel(ISettingsRepository settingsRepo)
    {
        _settingsRepo = settingsRepo;
    }

    public async Task LoadAsync()
    {
        _settings = await _settingsRepo.LoadAsync();
        DefaultOpenDirectory = _settings.DefaultOpenDirectory;
        GlobalAutoTailEnabled = _settings.GlobalAutoTailEnabled;
        EnableTabOverflowDropdown = _settings.EnableTabOverflowDropdown;
        DefaultFileEncoding = _settings.DefaultFileEncoding;
        LogFontFamily = NormalizeLogFont(_settings.LogFontFamily);

        var fallbacks = (_settings.FileEncodingFallbacks ?? new List<FileEncoding>())
            .Where(e => e != DefaultFileEncoding)
            .Distinct()
            .Take(3)
            .ToArray();
        FallbackEncoding1 = fallbacks.ElementAtOrDefault(0);
        FallbackEncoding2 = fallbacks.ElementAtOrDefault(1);
        FallbackEncoding3 = fallbacks.ElementAtOrDefault(2);

        HighlightRules.Clear();
        foreach (var rule in _settings.HighlightRules)
        {
            HighlightRules.Add(new HighlightRuleViewModel
            {
                Pattern = rule.Pattern,
                IsRegex = rule.IsRegex,
                CaseSensitive = rule.CaseSensitive,
                Color = rule.Color,
                IsEnabled = rule.IsEnabled
            });
        }
    }

    [RelayCommand]
    private void BrowseDefaultDirectory()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select default directory for opening log files",
            UseDescriptionForTitle = true
        };

        if (!string.IsNullOrEmpty(DefaultOpenDirectory) && Directory.Exists(DefaultOpenDirectory))
        {
            dialog.InitialDirectory = DefaultOpenDirectory;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DefaultOpenDirectory = dialog.SelectedPath;
        }
    }

    [RelayCommand]
    private void ClearDefaultDirectory()
    {
        DefaultOpenDirectory = null;
    }

    [RelayCommand]
    private void AddRule()
    {
        HighlightRules.Add(new HighlightRuleViewModel());
    }

    [RelayCommand]
    private void RemoveRule(HighlightRuleViewModel rule)
    {
        HighlightRules.Remove(rule);
    }

    public async Task SaveAsync()
    {
        _settings.DefaultOpenDirectory = DefaultOpenDirectory;
        _settings.GlobalAutoTailEnabled = GlobalAutoTailEnabled;
        _settings.EnableTabOverflowDropdown = EnableTabOverflowDropdown;
        _settings.DefaultFileEncoding = DefaultFileEncoding;
        _settings.FileEncodingFallbacks = new[] { FallbackEncoding1, FallbackEncoding2, FallbackEncoding3 }
            .Where(e => e.HasValue)
            .Select(e => e!.Value)
            .Where(e => e != DefaultFileEncoding)
            .Distinct()
            .ToList();
        _settings.LogFontFamily = NormalizeLogFont(LogFontFamily);
        _settings.HighlightRules = HighlightRules.Select(r => r.ToModel()).ToList();
        await _settingsRepo.SaveAsync(_settings);
    }

    private static string NormalizeLogFont(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
            return DefaultLogFont;
        return LogFontOptions.FirstOrDefault(f => string.Equals(f, fontFamily, StringComparison.OrdinalIgnoreCase))
               ?? DefaultLogFont;
    }
}
