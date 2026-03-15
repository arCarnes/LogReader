namespace LogReader.Infrastructure.Repositories;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonSessionRepository : ISessionRepository
{
    private const string FileName = "session.json";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<SessionState> LoadAsync()
    {
        await _lock.WaitAsync();
        try { return await JsonStore.LoadAsync<SessionState>(FileName); }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(SessionState state)
    {
        await _lock.WaitAsync();
        try
        {
            await JsonStore.SaveAsync(FileName, state).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }
}
