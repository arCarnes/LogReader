namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>Opt-in UI loading. Partial indexes stay privately owned until completion.</summary>
internal interface IInteractiveLogReaderService
{
    Task<IInteractiveIndexBuild> CreateIndexBuildAsync(string filePath, FileEncoding encoding, CancellationToken ct);

    Task<RecentLogPreview> ReadRecentPreviewAsync(string filePath, FileEncoding encoding, CancellationToken ct);
}

internal interface IInteractiveIndexBuild : IAsyncDisposable
{
    InteractiveIndexProgress Progress { get; }

    bool CanResume { get; }

    ValueTask<bool> AdvanceAsync(int maximumBytes, Func<bool> shouldYield, CancellationToken ct);

    ValueTask SuspendAsync();

    LineIndex TakeCompletedIndex();
}
