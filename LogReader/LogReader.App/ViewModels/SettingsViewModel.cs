namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class SettingsViewModel : ObservableObject
{
    private const string DefaultLogFont = "Consolas";
    private const int DefaultLogFontSize = 12;
    private const int MinLogFontSize = 8;
    private const int MaxLogFontSize = 18;

    public static IReadOnlyList<string> LogFontOptions { get; } = new[]
    {
        "Consolas",
        "Cascadia Mono",
        "Cascadia Code",
        "Lucida Console",
        "Courier New"
    };
    public static IReadOnlyList<int> LogFontSizeOptions { get; } = Enumerable.Range(MinLogFontSize, MaxLogFontSize - MinLogFontSize + 1).ToArray();

    private readonly ISettingsRepository _settingsRepo;
    private readonly IFolderDialogService _folderDialogService;
    private AppSettings _settings = new();

    [ObservableProperty]
    private string? _defaultOpenDirectory;

    [ObservableProperty]
    private string _logFontFamily = DefaultLogFont;

    [ObservableProperty]
    private int _logFontSize = DefaultLogFontSize;

    [ObservableProperty]
    private bool _showFullPathsInDashboard;

    public ObservableCollection<HighlightRuleViewModel> HighlightRules { get; } = new();
    public ObservableCollection<ReplacementPatternViewModel> DateRollingPatterns { get; } = new();
    public List<string> ColorPickerCustomColors { get; set; } = new();
    public ObservableCollection<string> RecentHighlightColors { get; } = new();

    public bool HasValidationErrors => DateRollingPatterns.Any(pattern => pattern.HasErrors);

    public SettingsViewModel(ISettingsRepository settingsRepo, IFolderDialogService? folderDialogService = null)
    {
        _settingsRepo = settingsRepo;
        _folderDialogService = folderDialogService ?? new FolderDialogService();
    }

    public async Task LoadAsync()
    {
        _settings = await _settingsRepo.LoadAsync();
        DefaultOpenDirectory = _settings.DefaultOpenDirectory;
        LogFontFamily = NormalizeLogFont(_settings.LogFontFamily);
        LogFontSize = NormalizeLogFontSize(_settings.LogFontSize);
        ShowFullPathsInDashboard = _settings.ShowFullPathsInDashboard;
        ColorPickerCustomColors = ColorDialogCustomColors.Normalize(_settings.ColorPickerCustomColors);
        RefreshRecentHighlightColors();

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

        DateRollingPatterns.Clear();
        foreach (var pattern in _settings.DateRollingPatterns ?? Enumerable.Empty<ReplacementPattern>())
            DateRollingPatterns.Add(ReplacementPatternViewModel.FromModel(pattern));
    }

    [RelayCommand]
    private void BrowseDefaultDirectory()
    {
        var initialDirectory = !string.IsNullOrEmpty(DefaultOpenDirectory) && Directory.Exists(DefaultOpenDirectory)
            ? DefaultOpenDirectory
            : null;
        var result = _folderDialogService.ShowFolderDialog(
            new FolderDialogRequest(
                "Select default directory for opening log files",
                initialDirectory));

        if (result.Accepted && !string.IsNullOrWhiteSpace(result.SelectedPath))
            DefaultOpenDirectory = result.SelectedPath;
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

    [RelayCommand]
    private void AddDateRollingPattern()
    {
        DateRollingPatterns.Add(new ReplacementPatternViewModel());
    }

    [RelayCommand]
    private void RemoveDateRollingPattern(ReplacementPatternViewModel pattern)
    {
        DateRollingPatterns.Remove(pattern);
    }

    [RelayCommand]
    private void MoveDateRollingPatternUp(ReplacementPatternViewModel pattern)
    {
        var index = DateRollingPatterns.IndexOf(pattern);
        if (index <= 0)
            return;

        DateRollingPatterns.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveDateRollingPatternDown(ReplacementPatternViewModel pattern)
    {
        var index = DateRollingPatterns.IndexOf(pattern);
        if (index < 0 || index >= DateRollingPatterns.Count - 1)
            return;

        DateRollingPatterns.Move(index, index + 1);
    }

    public async Task SaveAsync()
    {
        if (HasValidationErrors)
            throw new InvalidOperationException("One or more date rolling patterns have validation errors.");

        _settings.DefaultOpenDirectory = DefaultOpenDirectory;
        _settings.LogFontFamily = NormalizeLogFont(LogFontFamily);
        _settings.LogFontSize = NormalizeLogFontSize(LogFontSize);
        _settings.ShowFullPathsInDashboard = ShowFullPathsInDashboard;
        _settings.HighlightRules = HighlightRules.Select(r => r.ToModel()).ToList();
        _settings.ColorPickerCustomColors = ColorDialogCustomColors.Normalize(ColorPickerCustomColors);
        _settings.DateRollingPatterns = DateRollingPatterns.Select(pattern => pattern.ToModel()).ToList();
        await _settingsRepo.SaveAsync(_settings);
    }

    public void RememberHighlightColor(string? color)
    {
        ColorPickerCustomColors = ColorDialogCustomColors.AddRecentColor(ColorPickerCustomColors, color);
        RefreshRecentHighlightColors();
    }

    [RelayCommand]
    private void ClearRecentHighlightColors()
    {
        ColorPickerCustomColors.Clear();
        RefreshRecentHighlightColors();
    }

    private void RefreshRecentHighlightColors()
    {
        RecentHighlightColors.Clear();
        foreach (var color in ColorDialogCustomColors.ToNewestFirst(ColorPickerCustomColors))
            RecentHighlightColors.Add(color);
    }

    private static string NormalizeLogFont(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
            return DefaultLogFont;
        return LogFontOptions.FirstOrDefault(f => string.Equals(f, fontFamily, StringComparison.OrdinalIgnoreCase))
               ?? DefaultLogFont;
    }

    internal static int NormalizeLogFontSize(int fontSize)
    {
        if (fontSize <= 0)
            return DefaultLogFontSize;

        return Math.Clamp(fontSize, MinLogFontSize, MaxLogFontSize);
    }
}
