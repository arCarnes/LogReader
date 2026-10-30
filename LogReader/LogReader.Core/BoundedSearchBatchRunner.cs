namespace LogReader.Core;

using LogReader.Core.Models;

/// <summary>Runs bounded searches under caller-owned admission, retaining original result slots.</summary>
internal static class BoundedSearchBatchRunner
{
    internal static async Task<IReadOnlyList<SearchResult>> RunAsync(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<int>? workOrder,
        int maximumConcurrency,
        Func<string, CancellationToken, ValueTask<IDisposable>> acquireOperationAsync,
        Func<string, CancellationToken, Task<SearchResult>> searchAsync,
        CancellationToken ct)
    {
        var batch = await RunCoreAsync(filePaths, workOrder, maximumConcurrency,
            acquireOperationAsync, searchAsync, retainPartialResults: false, ct).ConfigureAwait(false);
        return batch.Results;
    }

    internal static async Task<BoundedSearchBatchResult> RunPartialAsync(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<int>? workOrder,
        int maximumConcurrency,
        Func<string, CancellationToken, ValueTask<IDisposable>> acquireOperationAsync,
        Func<string, CancellationToken, Task<SearchResult>> searchAsync,
        CancellationToken ct)
    {
        var batch = await RunCoreAsync(filePaths, workOrder, maximumConcurrency,
            acquireOperationAsync, searchAsync, retainPartialResults: true, ct).ConfigureAwait(false);
        return new BoundedSearchBatchResult(batch.Results, batch.WasCancelled);
    }

    private static async Task<(SearchResult[] Results, bool WasCancelled)> RunCoreAsync(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<int>? workOrder,
        int maximumConcurrency,
        Func<string, CancellationToken, ValueTask<IDisposable>> acquireOperationAsync,
        Func<string, CancellationToken, Task<SearchResult>> searchAsync,
        bool retainPartialResults,
        CancellationToken ct)
    {
        // Partial batches leave unfinished slots null; normal batches fill every slot before returning.
        var results = new SearchResult[filePaths.Count];
        var nextIndex = -1;
        var wasCancelled = 0;
        var workers = Enumerable.Range(0, Math.Min(maximumConcurrency, filePaths.Count))
            .Select(_ => RunWorkerAsync())
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return (results, Volatile.Read(ref wasCancelled) != 0);

        async Task RunWorkerAsync()
        {
            // Normal searches delegate cancellation to callbacks, which may handle it and return results.
            while (!retainPartialResults || !ct.IsCancellationRequested)
            {
                var workOrderIndex = Interlocked.Increment(ref nextIndex);
                if (workOrderIndex >= (workOrder?.Count ?? filePaths.Count))
                    return;

                var index = workOrder == null ? workOrderIndex : workOrder[workOrderIndex];
                try
                {
                    var filePath = filePaths[index];
                    using (await acquireOperationAsync(filePath, ct).ConfigureAwait(false))
                    {
                        var result = await searchAsync(filePath, ct).ConfigureAwait(false);
                        results[index] = result;
                        if (retainPartialResults && result.WasCancelled)
                            Interlocked.Exchange(ref wasCancelled, 1);
                    }
                }
                catch (OperationCanceledException) when (retainPartialResults && ct.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref wasCancelled, 1);
                    return;
                }
            }

            Interlocked.Exchange(ref wasCancelled, 1);
        }
    }
}
