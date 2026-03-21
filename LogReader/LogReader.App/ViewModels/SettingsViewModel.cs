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

    public static IReadOnlyList<string> LogFontOptions { get; } = new[]
    {
        "Consolas",
        "Cascadia Mono",
        "Cascadia Code",
        "Lucida Console",
        "Courier New"
    };

    private readonly ISettingsRepository _settingsRepo;
    private readonly IFolderDialogService _folderDialogService;
    private AppSettings _settings = new();

    [ObservableProperty]
    private string? _defaultOpenDirectory;

    [ObservableProperty]
    private string _logFontFamily = DefaultLogFont;

    public ObservableCollection<HighlightRuleViewModel> HighlightRules { get; } = new();

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

    public async Task SaveAsync()
    {
        _settings.DefaultOpenDirectory = DefaultOpenDirectory;
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
