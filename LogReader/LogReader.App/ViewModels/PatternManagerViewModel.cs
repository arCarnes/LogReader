namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    public ObservableCollection<ReplacementPatternViewModel> Patterns { get; } = new();

    [ObservableProperty]
    private ReplacementPatternViewModel? _selectedPattern;

    public bool HasValidationErrors => Patterns.Any(p => p.HasErrors);

    public PatternManagerViewModel(
        IReplacementPatternRepository patternRepo,
        IFileDialogService? fileDialogService = null)
    {
        _patternRepo = patternRepo;
        _fileDialogService = fileDialogService ?? new FileDialogService();
    }

    public async Task LoadAsync()
    {
        var patterns = await _patternRepo.LoadAsync();
        Patterns.Clear();
        foreach (var pattern in patterns)
            Patterns.Add(ReplacementPatternViewModel.FromModel(pattern));
    }

    public async Task SaveAsync()
    {
        var models = Patterns.Select(p => p.ToModel()).ToList();
        await _patternRepo.SaveAsync(models);
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
    private void ImportPatterns()
    {
        var result = _fileDialogService.ShowOpenFileDialog(new OpenFileDialogRequest(
            "Import Replacement Patterns",
            "JSON files (*.json)|*.json|All files (*.*)|*.*"));

        if (!result.Accepted || result.FileNames.Count == 0)
            return;

        try
        {
            var json = File.ReadAllText(result.FileNames[0]);
            var imported = JsonSerializer.Deserialize<List<ReplacementPattern>>(json, ExportJsonOptions);
            if (imported == null)
                return;

            foreach (var pattern in imported)
            {
                pattern.Id = Guid.NewGuid().ToString();
                Patterns.Add(ReplacementPatternViewModel.FromModel(pattern));
            }
        }
        catch (JsonException)
        {
            // Silently ignore malformed files for now.
        }
    }

    [RelayCommand]
    private void ExportPatterns()
    {
        var result = _fileDialogService.ShowSaveFileDialog(new SaveFileDialogRequest(
            "Export Replacement Patterns",
            "JSON files (*.json)|*.json|All files (*.*)|*.*",
            ".json",
            true,
            FileName: "patterns.json"));

        if (!result.Accepted || string.IsNullOrWhiteSpace(result.FileName))
            return;

        var models = Patterns.Select(p => p.ToModel()).ToList();
        var json = JsonSerializer.Serialize(models, ExportJsonOptions);
        File.WriteAllText(result.FileName, json);
    }
}
