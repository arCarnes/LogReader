namespace LogReader.Infrastructure.Services;

using System.Text.RegularExpressions;
using LogReader.Core.Models;

internal interface IResumableLogScanner
{
    Regex? PrepareRegex(SearchRequest request);

    Task<IResumableLogScan> OpenAsync(string path, ResumableFileCheckpoint checkpoint, CancellationToken ct);
}

internal interface IResumableLogScan : IAsyncDisposable
{
    Task<EvaluatedScanLine?> ReadNextAsync(SearchRequest request, Regex? regex,
        ScanWorkBudget budget, int maximumLineBytes, CancellationToken ct);

    void ValidateAfterRead();
}
