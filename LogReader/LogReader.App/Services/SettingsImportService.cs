namespace LogReader.App.Services;

using System.IO;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public interface ISettingsImportService
{
    Task<AppSettings> ImportSettingsAsync(string importPath);
}

internal sealed class SettingsImportService : ISettingsImportService
{
    private readonly ISettingsRepository _settingsRepository;

    public SettingsImportService(ISettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
    }

    public async Task<AppSettings> ImportSettingsAsync(string importPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importPath);

        var storedPath = GetImportedSettingsStoragePath(importPath);
        if (PathsReferToSameFile(importPath, storedPath))
            return await _settingsRepository.LoadFromFileAsync(storedPath);

        var tempPath = CreateImportingPath(storedPath);
        try
        {
            File.Copy(importPath, tempPath, overwrite: true);
            var settings = await _settingsRepository.LoadFromFileAsync(tempPath);
            File.Move(tempPath, storedPath, overwrite: true);
            return settings;
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static string CreateImportingPath(string storedPath)
        => storedPath + ".importing";

    private static string GetImportedSettingsStoragePath(string importPath)
    {
        var settingsDirectory = AppPaths.EnsureDirectory(AppPaths.SettingsDirectory);
        var fileName = Path.GetFileName(importPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "imported-settings.json";

        return Path.Combine(settingsDirectory, fileName);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static bool PathsReferToSameFile(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
