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

    /// <summary>Loads settings from an explicit external file path.</summary>
    Task<AppSettings> LoadFromFileAsync(string filePath);

    /// <summary>Saves settings to an explicit external file path.</summary>
    Task SaveToFileAsync(string filePath, AppSettings settings);
}
