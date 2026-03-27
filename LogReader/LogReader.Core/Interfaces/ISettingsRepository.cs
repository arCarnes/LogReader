namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Persists application settings such as open-directory defaults, display preferences, highlighting, and date rolling patterns.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>Loads settings, or returns defaults if no settings file exists.</summary>
    Task<AppSettings> LoadAsync();

    /// <summary>Saves the current settings.</summary>
    Task SaveAsync(AppSettings settings);
}
