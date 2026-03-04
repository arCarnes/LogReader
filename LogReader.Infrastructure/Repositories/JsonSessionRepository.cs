namespace LogReader.Infrastructure.Repositories;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonSessionRepository : ISessionRepository
{
    private const string FileName = "session.json";

    public async Task<SessionState> LoadAsync()
    {
        return await JsonStore.LoadAsync<SessionState>(FileName);
    }

    public async Task SaveAsync(SessionState state)
    {
        await JsonStore.SaveAsync(FileName, state).ConfigureAwait(false);
    }
}
