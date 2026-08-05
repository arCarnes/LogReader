namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Testing;

public sealed class LiveLogEndpointTests
{
    [Fact]
    public void TryStart_WhenListenerCannotBeCreated_IsFailSoftAndDoesNoLogWork()
    {
        var diagnostics = new List<string>();
        var catalog = new RecordingCatalogReader();
        var tail = new StubFileTailService();
        var logReader = new ChunkedLogReaderService();
        var registry = new FileSessionRegistry(logReader, tail, new FileEncodingDetectionService());
        var endpoint = new LiveLogEndpoint(
            logReader,
            new SearchService(),
            new FileEncodingDetectionService(),
            registry,
            () => catalog,
            backend => new LiveLogIpcServer(
                LiveLogPipeIdentityFactory.Create(@"C:\storage", "S-1-5-21-test"),
                backend,
                diagnostics.Add,
                () => throw new UnauthorizedAccessException(@"C:\private\pipe")),
            diagnostics.Add);

        try
        {
            var started = endpoint.TryStart();

            Assert.False(started);
            Assert.Equal(0, catalog.ReadCallCount);
            Assert.Equal(0, tail.StartCallCount);
            var sessions = registry.GetAgentProviderSnapshot(4, 2_000_000);
            Assert.Equal(0, sessions.ActiveSessions);
            Assert.Equal(0, sessions.RetainedSessions);
            Assert.Equal(0, sessions.MappedLineOffsets);
            Assert.All(diagnostics, message => Assert.DoesNotContain("private", message, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            endpoint.Dispose();
            registry.Dispose();
        }
    }

    [Fact]
    public void TryStart_WhenListenerIsIdle_OpensNoCatalogLogSessionWatcherOrTail()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var catalog = new RecordingCatalogReader();
        var tail = new StubFileTailService();
        var logReader = new ChunkedLogReaderService();
        var registry = new FileSessionRegistry(logReader, tail, new FileEncodingDetectionService());
        var identity = LiveLogPipeIdentityFactory.Create(
            @"C:\storage\" + Guid.NewGuid().ToString("N"),
            "S-1-5-21-test");
        var endpoint = new LiveLogEndpoint(
            logReader,
            new SearchService(),
            new FileEncodingDetectionService(),
            registry,
            () => catalog,
            backend => new LiveLogIpcServer(identity, backend));

        try
        {
            Assert.True(endpoint.TryStart());

            Thread.Sleep(50);
            Assert.Equal(0, catalog.ReadCallCount);
            Assert.Equal(0, tail.StartCallCount);
            Assert.Empty(tail.ActiveFiles);
            var sessions = registry.GetAgentProviderSnapshot(4, 2_000_000);
            Assert.Equal(0, sessions.ActiveSessions);
            Assert.Equal(0, sessions.RetainedSessions);
            Assert.Equal(0, sessions.MappedLineOffsets);
        }
        finally
        {
            endpoint.Dispose();
            registry.Dispose();
        }
    }

    private sealed class RecordingCatalogReader : IConfiguredLogCatalogReader, IDisposable
    {
        public int ReadCallCount { get; private set; }

        public Task<ConfiguredLogCatalogReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCallCount++;
            throw new InvalidOperationException("The idle listener must not read the catalog.");
        }

        public void Dispose()
        {
        }
    }
}
