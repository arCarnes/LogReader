namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class PatternManagerViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IReplacementPatternRepository _patternRepo;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageBoxService _messageBoxService;

    public ObservableCollection<ReplacementPatternViewModel> Patterns { get; } = new();

    [ObservableProperty]
    private ReplacementPatternViewModel? _selectedPattern;

    public bool HasValidationErrors => Patterns.Any(p => p.HasErrors);

    public PatternManagerViewModel(
        IReplacementPatternRepository patternRepo,
        IFileDialogService? fileDialogService = null,
        IMessageBoxService? messageBoxService = null)
    {
        _patternRepo = patternRepo;
        _fileDialogService = fileDialogService ?? new FileDialogService();
        _messageBoxService = messageBoxService ?? new MessageBoxService();
    }

    public async Task LoadAsync()
    {
        var patterns = await _patternRepo.LoadAsync();
        Patterns.Clear();
        foreach (var pattern in patterns)
            Patterns.Add(ReplacementPatternViewModel.FromModel(pattern));
    }

    public async Task<bool> TrySaveAsync(Window? owner = null)
    {
        if (HasValidationErrors)
        {
            ShowMessage(
                owner,
                "One or more date rolling patterns have validation errors. Please fix them before saving.",
                "Validation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var models = Patterns.Select(p => p.ToModel()).ToList();
        try
        {
            await _patternRepo.SaveAsync(models);
            return true;
        }
        catch (IOException ex)
        {
            ShowMessage(
                owner,
                $"Could not save date rolling patterns: {ex.Message}",
                "Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage(
                owner,
                $"Could not save date rolling patterns: {ex.Message}",
                "Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    [RelayCommand]
    private void AddPattern()
    {
        var pattern = new ReplacementPatternViewModel();
        Patterns.Add(pattern);
        SelectedPattern = pattern;
    }

    [RelayCommand]
    private void RemovePattern(ReplacementPatternViewModel pattern)
    {
        Patterns.Remove(pattern);
        if (SelectedPattern == pattern)
            SelectedPattern = Patterns.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ImportPatterns()
    {
        var result = _fileDialogService.ShowOpenFileDialog(new OpenFileDialogRequest(
            "Import Date Rolling Patterns",
            "JSON files (*.json)|*.json|All files (*.*)|*.*"));

        if (!result.Accepted || result.FileNames.Count == 0)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(result.FileNames[0]);
            var imported = DeserializePatternList(json);

            foreach (var pattern in imported)
            {
                pattern.Id = Guid.NewGuid().ToString();
                Patterns.Add(ReplacementPatternViewModel.FromModel(pattern));
            }
        }
        catch (JsonException ex)
        {
            ShowMessage(
                owner: null,
                $"Could not import date rolling patterns: {ex.Message}",
                "Import Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (IOException ex)
        {
            ShowMessage(
                owner: null,
                $"Could not import date rolling patterns: {ex.Message}",
                "Import Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage(
                owner: null,
                $"Could not import date rolling patterns: {ex.Message}",
                "Import Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ExportPatterns()
    {
        var result = _fileDialogService.ShowSaveFileDialog(new SaveFileDialogRequest(
            "Export Date Rolling Patterns",
            "JSON files (*.json)|*.json|All files (*.*)|*.*",
            ".json",
            true,
            FileName: "date-rolling-patterns.json"));

        if (!result.Accepted || string.IsNullOrWhiteSpace(result.FileName))
            return;

        var models = Patterns.Select(p => p.ToModel()).ToList();
        var json = JsonSerializer.Serialize(models, ExportJsonOptions);
        try
        {
            await File.WriteAllTextAsync(result.FileName, json);
        }
        catch (IOException ex)
        {
            ShowMessage(
                owner: null,
                $"Could not export date rolling patterns: {ex.Message}",
                "Export Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage(
                owner: null,
                $"Could not export date rolling patterns: {ex.Message}",
                "Export Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static List<ReplacementPattern> DeserializePatternList(string json)
    {
        var imported = JsonSerializer.Deserialize<List<ReplacementPattern>>(json, ExportJsonOptions);
        if (imported != null)
            return imported;

        throw new JsonException("The selected file did not contain a date rolling pattern list.");
    }

    private void ShowMessage(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        if (owner == null)
        {
            _messageBoxService.Show(message, caption, buttons, image);
            return;
        }

        _messageBoxService.Show(owner, message, caption, buttons, image);
    }
}
