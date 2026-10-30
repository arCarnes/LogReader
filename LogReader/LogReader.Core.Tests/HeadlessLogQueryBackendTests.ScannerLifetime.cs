namespace LogReader.Core.Tests;

using System.Text.RegularExpressions;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed partial class HeadlessLogQueryBackendTests
{
    public enum ScanGate { Open, Read, Dispose }

    [Theory]
    [InlineData(false, ScanGate.Open)]
    [InlineData(false, ScanGate.Read)]
    [InlineData(false, ScanGate.Dispose)]
    [InlineData(true, ScanGate.Open)]
    [InlineData(true, ScanGate.Read)]
    [InlineData(true, ScanGate.Dispose)]
    public async Task ScannerCancellation_DrainsOwnedSessions(bool count, ScanGate gate)
    {
        var path = await CreateFileAsync("scanner-cancellation.log", "needle\nother");
        var scanner = new GatedScanner(gate);
        using var backend = CreateBackend(CreateSnapshot(("file", path)), scanner: scanner);
        using var cancellation = new CancellationTokenSource();
        Task responseTask;
        string? errorCode = null;
        async Task RunAsync()
        {
            errorCode = count
                ? Assert.Single((await backend.CountLogsAsync(new LogCountQuery
                {
                    Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle"
                }, cancellation.Token)).Errors).Code
                : Assert.Single((await backend.SearchLogsAsync(Search("file", "needle", ConfiguredLogTargetKind.LogFile),
                    cancellation.Token)).Errors).Code;
        }
        responseTask = RunAsync();
        try
        {
            await scanner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            if (gate == ScanGate.Dispose)
                Assert.False(responseTask.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            scanner.Release.TrySetResult();
            await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal("request_cancelled", errorCode);
        Assert.Equal(gate == ScanGate.Open ? 0 : 1, scanner.Opened);
        Assert.Equal(scanner.Opened, scanner.Disposed);
    }

    private sealed class GatedScanner(ScanGate gate) : IResumableLogScanner
    {
        private readonly IResumableLogScanner _inner = new ResumableLogScanner(new FileEncodingDetectionService());
        private int _opened;
        private int _disposed;
        public int Opened => Volatile.Read(ref _opened);
        public int Disposed => Volatile.Read(ref _disposed);
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Regex? PrepareRegex(SearchRequest request) => _inner.PrepareRegex(request);

        private async Task WaitAsync(ScanGate phase, CancellationToken ct)
        {
            if (gate != phase)
                return;
            Ready.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }

        public async Task<IResumableLogScan> OpenAsync(string path, ResumableFileCheckpoint checkpoint, CancellationToken ct)
        {
            await WaitAsync(ScanGate.Open, ct);
            var inner = await _inner.OpenAsync(path, checkpoint, ct);
            Interlocked.Increment(ref _opened);
            return new Scan(this, inner);
        }

        private sealed class Scan(GatedScanner owner, IResumableLogScan inner) : IResumableLogScan
        {
            private int _disposed;

            public async Task<EvaluatedScanLine?> ReadNextAsync(SearchRequest request, Regex? regex,
                ScanWorkBudget budget, int maximumLineBytes, CancellationToken ct)
            {
                await owner.WaitAsync(ScanGate.Read, ct);
                return await inner.ReadNextAsync(request, regex, budget, maximumLineBytes, ct);
            }

            public void ValidateAfterRead() => inner.ValidateAfterRead();

            public async ValueTask DisposeAsync()
            {
                Assert.Equal(0, Interlocked.Exchange(ref _disposed, 1));
                await owner.WaitAsync(ScanGate.Dispose, CancellationToken.None);
                await inner.DisposeAsync();
                Interlocked.Increment(ref owner._disposed);
            }
        }
    }
}
