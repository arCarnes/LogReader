namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Persists application settings (default open directory, etc.).
/// </summary>
public interface ISettingsRepository
{
    /// <summary>Loads settings, or returns defaults if no settings file exists.</summary>
    Task<AppSettings> LoadAsync();

    /// <summary>Saves the current settings.</summary>
    Task SaveAsync(AppSettings settings);
}
