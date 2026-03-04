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
    private readonly ISettingsRepository _settingsRepo;
    private AppSettings _settings = new();

    [ObservableProperty]
    private string? _defaultOpenDirectory;

    public ObservableCollection<HighlightRuleViewModel> HighlightRules { get; } = new();

    public SettingsViewModel(ISettingsRepository settingsRepo)
    {
        _settingsRepo = settingsRepo;
    }

    public async Task LoadAsync()
    {
        _settings = await _settingsRepo.LoadAsync();
        DefaultOpenDirectory = _settings.DefaultOpenDirectory;

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
        _settings.HighlightRules = HighlightRules.Select(r => r.ToModel()).ToList();
        await _settingsRepo.SaveAsync(_settings);
    }
}
