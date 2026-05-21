namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class SettingsViewModel : ObservableObject
{
    private const string DefaultLogFont = "Consolas";
    private const int DefaultLogFontSize = 12;
    private const int MinLogFontSize = 8;
    private const int MaxLogFontSize = 18;
    private const string DefaultSearchMatchHighlightColor = "#FFF59D";
    private const string SettingsFileFilter = "LogReader Settings (*.json)|*.json";

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
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly ISettingsImportService _settingsImportService;
    private AppSettings _settings = new();

    [ObservableProperty]
    private string? _defaultOpenDirectory;

    [ObservableProperty]
    private string _logFontFamily = DefaultLogFont;

    [ObservableProperty]
    private int _logFontSize = DefaultLogFontSize;

    [ObservableProperty]
    private bool _showFullPathsInDashboard;

    [ObservableProperty]
    private bool _enableSearchMatchHighlighting = true;

    [ObservableProperty]
    private string _searchMatchHighlightColor = DefaultSearchMatchHighlightColor;

    public ObservableCollection<HighlightRuleViewModel> HighlightRules { get; } = new();
    public ObservableCollection<ReplacementPatternViewModel> DateRollingPatterns { get; } = new();
    public List<string> ColorPickerCustomColors { get; set; } = new();
    public ObservableCollection<string> RecentHighlightColors { get; } = new();

    public bool HasValidationErrors => DateRollingPatterns.Any(pattern => pattern.HasErrors);

    public SettingsViewModel(
        ISettingsRepository settingsRepo,
        IFolderDialogService? folderDialogService = null,
        IFileDialogService? fileDialogService = null,
        IMessageBoxService? messageBoxService = null,
        ISettingsImportService? settingsImportService = null)
    {
        _settingsRepo = settingsRepo;
        _folderDialogService = folderDialogService ?? new FolderDialogService();
        _fileDialogService = fileDialogService ?? new FileDialogService();
        _messageBoxService = messageBoxService ?? new MessageBoxService();
        _settingsImportService = settingsImportService ?? new SettingsImportService(settingsRepo);
    }

    public async Task LoadAsync()
    {
        _settings = await _settingsRepo.LoadAsync();
        ApplySettings(_settings);
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
    private async Task ImportSettings()
    {
        var result = _fileDialogService.ShowOpenFileDialog(
            new OpenFileDialogRequest(
                "Import Settings",
                SettingsFileFilter,
                InitialDirectory: GetSettingsImportExportDirectory()));

        if (!result.Accepted || result.FileNames.Count == 0)
            return;

        try
        {
            var importedSettings = await _settingsImportService.ImportSettingsAsync(result.FileNames[0]);
            _settings = importedSettings;
            ApplySettings(_settings);
        }
        catch (Exception ex) when (IsSettingsImportExportException(ex))
        {
            _messageBoxService.Show(
                $"Could not import the selected settings file.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Import Settings Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ExportSettings()
    {
        if (HasValidationErrors)
        {
            _messageBoxService.Show(
                "One or more date rolling patterns have validation errors. Please fix them before exporting settings.",
                "Export Settings Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var result = _fileDialogService.ShowSaveFileDialog(
            new SaveFileDialogRequest(
                "Export Settings",
                SettingsFileFilter,
                ".json",
                AddExtension: true,
                InitialDirectory: GetSettingsImportExportDirectory(),
                FileName: CreateDefaultSettingsExportFileName()));

        if (!result.Accepted || string.IsNullOrWhiteSpace(result.FileName))
            return;

        try
        {
            await _settingsRepo.SaveToFileAsync(result.FileName, BuildSettingsFromInputs());
        }
        catch (Exception ex) when (IsSettingsImportExportException(ex))
        {
            _messageBoxService.Show(
                $"Could not export settings to the selected file.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Export Settings Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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

        _settings = BuildSettingsFromInputs();
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

    private void ApplySettings(AppSettings settings)
    {
        DefaultOpenDirectory = settings.DefaultOpenDirectory;
        LogFontFamily = NormalizeLogFont(settings.LogFontFamily);
        LogFontSize = NormalizeLogFontSize(settings.LogFontSize);
        ShowFullPathsInDashboard = settings.ShowFullPathsInDashboard;
        EnableSearchMatchHighlighting = settings.EnableSearchMatchHighlighting;
        SearchMatchHighlightColor = NormalizeSearchMatchHighlightColor(settings.SearchMatchHighlightColor);
        ColorPickerCustomColors = ColorDialogCustomColors.Normalize(settings.ColorPickerCustomColors);
        RefreshRecentHighlightColors();

        HighlightRules.Clear();
        foreach (var rule in settings.HighlightRules)
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
        foreach (var pattern in settings.DateRollingPatterns ?? Enumerable.Empty<ReplacementPattern>())
            DateRollingPatterns.Add(ReplacementPatternViewModel.FromModel(pattern));
    }

    private AppSettings BuildSettingsFromInputs()
        => new()
        {
            DefaultOpenDirectory = DefaultOpenDirectory,
            LogFontFamily = NormalizeLogFont(LogFontFamily),
            LogFontSize = NormalizeLogFontSize(LogFontSize),
            ShowFullPathsInDashboard = ShowFullPathsInDashboard,
            EnableSearchMatchHighlighting = EnableSearchMatchHighlighting,
            SearchMatchHighlightColor = NormalizeSearchMatchHighlightColor(SearchMatchHighlightColor),
            HighlightRules = HighlightRules.Select(r => r.ToModel()).ToList(),
            ColorPickerCustomColors = ColorDialogCustomColors.Normalize(ColorPickerCustomColors),
            DateRollingPatterns = DateRollingPatterns.Select(pattern => pattern.ToModel()).ToList()
        };

    private static string GetSettingsImportExportDirectory()
        => AppPaths.EnsureDirectory(AppPaths.SettingsDirectory);

    internal static string CreateDefaultSettingsExportFileName()
        => $"logreader-settings-{DateTime.Now:yyyy-MM-dd-HHmmss}.json";

    private static bool IsSettingsImportExportException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidDataException;

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

    internal static string NormalizeSearchMatchHighlightColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return DefaultSearchMatchHighlightColor;

        var hex = color.Trim();
        if (hex.Length != 7 || hex[0] != '#')
            return DefaultSearchMatchHighlightColor;

        return int.TryParse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? hex.ToUpperInvariant()
            : DefaultSearchMatchHighlightColor;
    }
}
