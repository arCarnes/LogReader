namespace LogReader.Core.Tests;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed partial class HeadlessLogQueryBackendTests
{
    // Fixture descriptions are expanded into line events. All query reduction, admission,
    // continuation and error handling still run through the production resumable orchestration.
    private class ScriptedLogScanner(
        Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>>? handler = null) : IResumableLogScanner
    {
        private readonly ConcurrentDictionary<FileGenerationToken, Task<SearchResult>> _scripts = new();
        private long _nextGeneration;
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public IEncodingDetectionService? EncodingDetection { get; set; }

        public Regex? PrepareRegex(SearchRequest request)
            => request.IsRegex ? RegexPatternFactory.Create(request.Query, request.CaseSensitive) : null;

        public Task<IResumableLogScan> OpenAsync(string path, ResumableFileCheckpoint checkpoint, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!checkpoint.Initialized)
            {
                checkpoint.Encoding = EncodingDetection?.ResolveEncodingDecision(path, FileEncoding.Auto).ResolvedEncoding ?? FileEncoding.Utf8;
                checkpoint.Generation = FileGenerationToken.Create(1, (ulong)Interlocked.Increment(ref _nextGeneration));
                checkpoint.Initialized = true;
            }
            return Task.FromResult<IResumableLogScan>(new ScriptedScan(this, path, checkpoint));
        }

        protected virtual Task<SearchResult> PrepareAsync(string path, SearchRequest request, FileEncoding encoding, CancellationToken ct)
            => handler!(path, request, encoding, ct);

        private sealed class ScriptedScan(ScriptedLogScanner owner, string path, ResumableFileCheckpoint checkpoint) : IResumableLogScan
        {
            public async Task<EvaluatedScanLine?> ReadNextAsync(SearchRequest request, Regex? regex,
                ScanWorkBudget budget, int maximumLineBytes, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var result = await owner._scripts.GetOrAdd(checkpoint.Generation, _ =>
                {
                    Interlocked.Increment(ref owner._callCount);
                    return owner.PrepareAsync(path, request, checkpoint.Encoding, ct);
                });
                if (result.Error != null)
                    throw new IOException(result.Error);
                var lineCount = Math.Max(result.EvaluatedThroughLine ?? 0,
                    Math.Max(result.MatchingLineCount, result.Hits.Count == 0 ? 0 : result.Hits.Max(hit => hit.LineNumber)));
                checkpoint.Extent = result.ScannedFileSize ?? lineCount;
                if (checkpoint.LineNumber > lineCount)
                {
                    if (!result.IsEvaluationComplete && result.EvaluatedThroughLine.HasValue)
                        checkpoint.Reasons.Add("evaluation_incomplete");
                    checkpoint.Done = true;
                    return null;
                }
                var number = checkpoint.LineNumber++;
                var hit = result.Hits.FirstOrDefault(hit => hit.LineNumber == number);
                var occurrences = hit != null || number <= result.MatchingLineCount
                    ? (int)(number == 1 ? Math.Max(1, result.MatchOccurrenceCount - Math.Max(0, result.MatchingLineCount - 1)) : 1)
                    : 0;
                checkpoint.ReadOffset++;
                budget.AddBytes(1);
                if (checkpoint.LineNumber > lineCount)
                {
                    if (!result.IsEvaluationComplete && result.EvaluatedThroughLine.HasValue)
                        checkpoint.Reasons.Add("evaluation_incomplete");
                    checkpoint.Done = true;
                }
                return new EvaluatedScanLine(number, hit?.LineText ?? "", request.MaxHitsPerFile == 0 ? null : hit,
                    occurrences, null, 1);
            }

            public void ValidateAfterRead() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BeforeValidationScanner(IResumableLogScanner scanner, Action beforeValidation) : IResumableLogScanner
    {
        private readonly Action _beforeValidation = beforeValidation;
        private int _validated;
        public Exception? ValidationError { get; private set; }
        public Regex? PrepareRegex(SearchRequest request) => scanner.PrepareRegex(request);

        public async Task<IResumableLogScan> OpenAsync(string path, ResumableFileCheckpoint checkpoint, CancellationToken ct)
            => new Scan(this, await scanner.OpenAsync(path, checkpoint, ct));

        private sealed class Scan(BeforeValidationScanner owner, IResumableLogScan inner) : IResumableLogScan
        {
            public Task<EvaluatedScanLine?> ReadNextAsync(SearchRequest request, Regex? regex,
                ScanWorkBudget budget, int maximumLineBytes, CancellationToken ct)
                => inner.ReadNextAsync(request, regex, budget, maximumLineBytes, ct);

            public void ValidateAfterRead()
            {
                try
                {
                    if (Interlocked.Exchange(ref owner._validated, 1) == 0)
                        owner._beforeValidation();
                    inner.ValidateAfterRead();
                }
                catch (Exception ex)
                {
                    owner.ValidationError = ex;
                    throw;
                }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class ConcurrencyTrackingScanner(TimeSpan delay) : ScriptedLogScanner
    {
        private int _active;
        private int _maximumConcurrency;
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        protected override async Task<SearchResult> PrepareAsync(string path, SearchRequest request, FileEncoding encoding, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            var maximum = Volatile.Read(ref _maximumConcurrency);
            while (active > maximum)
            {
                var previous = Interlocked.CompareExchange(ref _maximumConcurrency, active, maximum);
                if (previous == maximum)
                    break;
                maximum = previous;
            }
            try
            {
                await Task.Delay(delay, ct);
                return Result(path, "hit");
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
