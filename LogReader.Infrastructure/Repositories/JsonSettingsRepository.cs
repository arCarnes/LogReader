namespace LogReader.Infrastructure.Repositories;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonSettingsRepository : ISettingsRepository
{
    private const string FileName = "settings.json";

    public Task<AppSettings> LoadAsync() => JsonStore.LoadAsync<AppSettings>(FileName);

    public Task SaveAsync(AppSettings settings) => JsonStore.SaveAsync(FileName, settings);
}
