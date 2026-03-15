namespace LogReader.Infrastructure.Repositories;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonSettingsRepository : ISettingsRepository
{
    private const string FileName = "settings.json";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<AppSettings> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { return await JsonStore.LoadAsync<AppSettings>(FileName).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await JsonStore.SaveAsync(FileName, settings).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }
}
