namespace LogReader.Infrastructure.Services;

using System.Collections.Concurrent;
using System.Text;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class FileTailService : IFileTailService
{
    private readonly ConcurrentDictionary<string, TailState> _tailedFiles = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<TailEventArgs>? LinesAppended;
    public event EventHandler<FileRotatedEventArgs>? FileRotated;

    public void StartTailing(string filePath, FileEncoding encoding)
    {
        if (_tailedFiles.ContainsKey(filePath)) return;

        var cts = new CancellationTokenSource();
        var state = new TailState
        {
            FilePath = filePath,
            Encoding = encoding,
            Cts = cts
        };

        if (_tailedFiles.TryAdd(filePath, state))
        {
            state.Task = Task.Run(() => TailLoopAsync(state, cts.Token));
        }
    }

    public void StopTailing(string filePath)
    {
        if (_tailedFiles.TryRemove(filePath, out var state))
        {
            state.Cts.Cancel();
            try { state.Task.Wait(TimeSpan.FromSeconds(2)); } catch { }
            state.Cts.Dispose();
        }
    }

    public void StopAll()
    {
        foreach (var key in _tailedFiles.Keys.ToList())
        {
            StopTailing(key);
        }
    }

    public void Dispose()
    {
        StopAll();
    }

    private async Task TailLoopAsync(TailState state, CancellationToken ct)
    {
        long lastSize = 0;
        string? lastCreationTimeId = null;

        try
        {
            // Get initial file state
            if (File.Exists(state.FilePath))
            {
                var info = new FileInfo(state.FilePath);
                lastSize = info.Length;
                lastCreationTimeId = GetFileIdentity(state.FilePath);
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct); // Poll every 250ms

                if (!File.Exists(state.FilePath))
                {
                    // File was deleted - might be rotation in progress
                    await Task.Delay(500, ct); // Wait a bit for new file
                    if (File.Exists(state.FilePath))
                    {
                        // File recreated - rotation detected
                        FileRotated?.Invoke(this, new FileRotatedEventArgs
                        {
                            FilePath = state.FilePath
                        });
                        lastSize = 0;
                        lastCreationTimeId = GetFileIdentity(state.FilePath);
                    }
                    continue;
                }

                var fileInfo = new FileInfo(state.FilePath);
                var currentSize = fileInfo.Length;
                var currentIdentity = GetFileIdentity(state.FilePath);

                // Rotation detection: file identity changed (creation time changed = new file)
                if (lastCreationTimeId != null && currentIdentity != lastCreationTimeId)
                {
                    FileRotated?.Invoke(this, new FileRotatedEventArgs
                    {
                        FilePath = state.FilePath
                    });
                    lastSize = 0;
                    lastCreationTimeId = currentIdentity;
                }
                // File was truncated (smaller than before) - also a rotation/reset
                else if (currentSize < lastSize)
                {
                    FileRotated?.Invoke(this, new FileRotatedEventArgs
                    {
                        FilePath = state.FilePath
                    });
                    lastSize = 0;
                    lastCreationTimeId = currentIdentity;
                }

                // Notify if file grew
                if (currentSize > lastSize)
                {
                    LinesAppended?.Invoke(this, new TailEventArgs
                    {
                        FilePath = state.FilePath
                    });
                    lastSize = currentSize;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* silently stop tailing on unexpected errors */ }
    }

    private static string? GetFileIdentity(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.CreationTimeUtc.Ticks.ToString();
        }
        catch
        {
            return null;
        }
    }

    private class TailState
    {
        public string FilePath { get; init; } = string.Empty;
        public FileEncoding Encoding { get; init; }
        public CancellationTokenSource Cts { get; init; } = null!;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
