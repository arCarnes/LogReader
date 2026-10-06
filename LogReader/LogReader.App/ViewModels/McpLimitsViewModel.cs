namespace LogReader.App.ViewModels;

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal partial class McpLimitsViewModel : ObservableObject
{
    private readonly ISettingsRepository _repository;
    [ObservableProperty] private string _maximumSessions = "16";
    [ObservableProperty] private string _memoryPerQueryMiB = "64";
    [ObservableProperty] private string _totalMemoryMiB = "256";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _status = "Loading saved limits…";

    public McpLimitsViewModel(ISettingsRepository repository) => _repository = repository;

    public string? ValidationError => Parse(out var limits) ? limits.Validate() :
        "Enter positive whole numbers up to 2,147,483,647 for all three limits.";
    public bool CanEdit => IsReady && !IsSaving;
    public bool CanSave => CanEdit && ValidationError == null;

    partial void OnMaximumSessionsChanged(string value) => InputsChanged();
    partial void OnMemoryPerQueryMiBChanged(string value) => InputsChanged();
    partial void OnTotalMemoryMiBChanged(string value) => InputsChanged();
    partial void OnIsReadyChanged(bool value) => RefreshState();
    partial void OnIsSavingChanged(bool value) => RefreshState();

    private void InputsChanged()
    {
        Status = string.Empty;
        OnPropertyChanged(nameof(ValidationError));
        RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
        RestoreDefaultsCommand.NotifyCanExecuteChanged();
    }

    private bool Parse(out McpContinuationLimits limits)
    {
        var valid = int.TryParse(MaximumSessions, NumberStyles.None, CultureInfo.InvariantCulture, out var sessions)
            & int.TryParse(MemoryPerQueryMiB, NumberStyles.None, CultureInfo.InvariantCulture, out var query)
            & int.TryParse(TotalMemoryMiB, NumberStyles.None, CultureInfo.InvariantCulture, out var total);
        limits = new() { MaximumSessions = sessions, MemoryPerQueryMiB = query, TotalMemoryMiB = total };
        return valid;
    }

    private void Apply(McpContinuationLimits limits)
    {
        MaximumSessions = limits.MaximumSessions.ToString(CultureInfo.InvariantCulture);
        MemoryPerQueryMiB = limits.MemoryPerQueryMiB.ToString(CultureInfo.InvariantCulture);
        TotalMemoryMiB = limits.TotalMemoryMiB.ToString(CultureInfo.InvariantCulture);
    }

    public async Task LoadAsync()
    {
        try
        {
            var settings = await _repository.LoadAsync();
            Apply(settings.McpContinuationLimits ?? new McpContinuationLimits());
            IsReady = true;
            Status = string.Empty;
        }
        catch (Exception)
        {
            Status = "Saved limits could not be loaded. Close this dialog and check your storage settings.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RestoreDefaults() => Apply(new McpContinuationLimits());

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave || !Parse(out var limits))
            return;
        IsSaving = true;
        try
        {
            var settings = await _repository.LoadAsync();
            settings.McpContinuationLimits = limits;
            await _repository.SaveAsync(settings);
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
