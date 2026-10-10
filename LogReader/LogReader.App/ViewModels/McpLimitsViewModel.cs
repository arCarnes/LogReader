namespace LogReader.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed record McpCapacityProfile(string Name, McpContinuationLimits Limits);

internal partial class McpLimitsViewModel : ObservableObject
{
    public static IReadOnlyList<McpCapacityProfile> Profiles { get; } = Array.AsReadOnly(new[]
    {
        new McpCapacityProfile("Low memory", new McpContinuationLimits
            { MaximumSessions = 8, MemoryPerQueryMiB = 32, TotalMemoryMiB = 128 }),
        new McpCapacityProfile("Standard", new McpContinuationLimits()),
        new McpCapacityProfile("Higher capacity", new McpContinuationLimits
            { MaximumSessions = 32, MemoryPerQueryMiB = 128, TotalMemoryMiB = 512 })
    });

    private readonly ISettingsRepository _repository;
    private McpContinuationLimits _savedLimits = new();
    [ObservableProperty] private McpCapacityProfile? _selectedProfile;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _status = "Loading saved limits…";

    public McpLimitsViewModel(ISettingsRepository repository) => _repository = repository;

    public IReadOnlyList<McpCapacityProfile> AvailableProfiles => Profiles;
    private McpContinuationLimits DisplayedLimits => SelectedProfile?.Limits ?? _savedLimits;
    public int MaximumSessions => DisplayedLimits.MaximumSessions;
    public int MemoryPerQueryMiB => DisplayedLimits.MemoryPerQueryMiB;
    public int TotalMemoryMiB => DisplayedLimits.TotalMemoryMiB;
    public string? ProfileNotice => IsReady && SelectedProfile == null
        ? "Saved limits do not match a profile. Choose a profile to replace them."
        : null;
    public bool CanEdit => IsReady && !IsSaving;
    public bool CanSave => CanEdit && SelectedProfile != null && Profiles.Contains(SelectedProfile);

    partial void OnSelectedProfileChanged(McpCapacityProfile? value) => InputsChanged();
    partial void OnIsReadyChanged(bool value) => RefreshState();
    partial void OnIsSavingChanged(bool value) => RefreshState();

    private void InputsChanged()
    {
        Status = string.Empty;
        OnPropertyChanged(nameof(MaximumSessions));
        OnPropertyChanged(nameof(MemoryPerQueryMiB));
        OnPropertyChanged(nameof(TotalMemoryMiB));
        RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ProfileNotice));
        SaveCommand.NotifyCanExecuteChanged();
        RestoreDefaultsCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadAsync()
    {
        IsReady = false;
        try
        {
            var settings = await _repository.LoadAsync();
            _savedLimits = settings.McpContinuationLimits ?? new McpContinuationLimits();
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Limits == _savedLimits);
            InputsChanged();
            IsReady = true;
            Status = string.Empty;
        }
        catch (Exception)
        {
            Status = "Saved limits could not be loaded. Close this dialog and check your storage settings.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RestoreDefaults() => SelectedProfile = Profiles[1];

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave)
            return;
        var limits = SelectedProfile!.Limits;
        IsSaving = true;
        try
        {
            var settings = await _repository.LoadAsync();
            settings.McpContinuationLimits = limits;
            await _repository.SaveAsync(settings);
            _savedLimits = limits;
            Status = "Limits saved. Restart the MCP server through your MCP client to apply these limits.";
        }
        catch (Exception)
        {
            Status = "Limits could not be saved. Check your storage access and try again.";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
