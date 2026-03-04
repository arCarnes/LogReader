namespace LogReader.Infrastructure.Repositories;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonLogFileRepository : ILogFileRepository
{
    private const string FileName = "logfiles.json";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<LogFileEntry>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try { return await JsonStore.LoadAsync<List<LogFileEntry>>(FileName); }
        finally { _lock.Release(); }
    }

    public async Task<LogFileEntry?> GetByIdAsync(string id)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(f => f.Id == id);
    }

    public async Task<LogFileEntry?> GetByPathAsync(string filePath)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddAsync(LogFileEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await JsonStore.LoadAsync<List<LogFileEntry>>(FileName);
            all.Add(entry);
            await JsonStore.SaveAsync(FileName, all);
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateAsync(LogFileEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await JsonStore.LoadAsync<List<LogFileEntry>>(FileName);
            var idx = all.FindIndex(f => f.Id == entry.Id);
            if (idx >= 0) all[idx] = entry;
            await JsonStore.SaveAsync(FileName, all);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await JsonStore.LoadAsync<List<LogFileEntry>>(FileName);
            all.RemoveAll(f => f.Id == id);
            await JsonStore.SaveAsync(FileName, all);
        }
        finally { _lock.Release(); }
    }
}
