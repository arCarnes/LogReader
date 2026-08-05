namespace LogReader.Tests;

using System.ComponentModel;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Testing;

public class FileSessionRegistryTests
{
    [Fact]
    public void Acquire_SamePathAndRequestedEncoding_ReusesOneSession()
    {
        var registry = CreateRegistry();

        var lease1 = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf8);
        var lease2 = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf8);

        try
        {
            Assert.Same(lease1.Session, lease2.Session);
            Assert.Equal(1, registry.ActiveSessionCount);
            Assert.Equal(0, lease1.Session.DebugIsDisposed);
        }
        finally
        {
            lease2.Dispose();
            lease1.Dispose();
        }
    }

    [Fact]
    public void Acquire_SamePathWithDifferentRequestedEncodings_CreatesDistinctSessions()
    {
        var registry = CreateRegistry();

        var lease1 = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf8);
        var lease2 = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf16);

        try
        {
            Assert.NotSame(lease1.Session, lease2.Session);
            Assert.Equal(2, registry.ActiveSessionCount);
        }
        finally
        {
            lease2.Dispose();
            lease1.Dispose();
        }
    }

    [Fact]
    public void Acquire_AutoAndManualUtf8_DoNotShareOneSession()
    {
        var registry = CreateRegistry();

        var autoLease = registry.Acquire(@"C:\test\shared.log", FileEncoding.Auto);
        var utf8Lease = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf8);

        try
        {
            Assert.NotSame(autoLease.Session, utf8Lease.Session);
            Assert.Equal(2, registry.ActiveSessionCount);
        }
        finally
        {
            utf8Lease.Dispose();
            autoLease.Dispose();
        }
    }

    [Fact]
    public void Release_LastLease_KeepsSessionWarmForRecentReopen()
    {
        var registry = CreateRegistry();
        var lease = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf8);
        var session = lease.Session;

        Assert.Equal(1, registry.ActiveSessionCount);

        lease.Dispose();

        Assert.Equal(0, registry.ActiveSessionCount);
        Assert.Equal(1, registry.RetainedSessionCount);
        Assert.Equal(0, session.DebugIsDisposed);

        registry.Dispose();
    }

    [Fact]
    public void Acquire_RecentlyReleasedSession_ReusesWarmSession()
    {
        var registry = CreateRegistry();
        var lease = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf8);
        var session = lease.Session;

        lease.Dispose();

        var reopenedLease = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf8);
        try
        {
            Assert.Same(session, reopenedLease.Session);
            Assert.Equal(1, registry.ActiveSessionCount);
            Assert.Equal(0, registry.RetainedSessionCount);
            Assert.Equal(0, session.DebugIsDisposed);
        }
        finally
        {
            reopenedLease.Dispose();
            registry.Dispose();
        }
    }

    [Fact]
    public void SweepExpiredSessions_DisposesIdleWarmSessions()
    {
        var registry = CreateRegistry();
        var lease = registry.Acquire(@"C:\test\shared.log", FileEncoding.Utf8);
        var session = lease.Session;

        lease.Dispose();
        var disposedCount = registry.SweepExpiredSessions(DateTime.UtcNow + registry.WarmRetentionDuration + TimeSpan.FromSeconds(1));

        Assert.Equal(1, disposedCount);
        Assert.Equal(0, registry.ActiveSessionCount);
        Assert.Equal(0, registry.RetainedSessionCount);
        Assert.Equal(1, session.DebugIsDisposed);

        registry.Dispose();
    }

    [Fact]
    public async Task EncodingChange_PreservesTabObjectAndLocalState()
    {
        var reader = new EncodingAwareLogReaderService();
        var tailService = new StubFileTailService();
        var detection = new StubEncodingDetectionService
        {
            AutoDetectedEncoding = FileEncoding.Utf8,
            AutoStatusText = "Auto -> UTF-8"
        };
        var registry = new FileSessionRegistry(reader, tailService, detection);
        var tab = CreateTab(reader, tailService, detection, registry, FileEncoding.Auto);

        try
        {
            await tab.LoadAsync();
            var originalTab = tab;

            tab.IsPinned = true;
            tab.AutoScrollEnabled = false;

            await ChangeEncodingAndWaitForLoadAsync(tab, FileEncoding.Utf16);

            Assert.Same(originalTab, tab);
            Assert.True(tab.IsPinned);
            Assert.False(tab.AutoScrollEnabled);
        }
        finally
        {
            tab.Dispose();
        }
    }

    [Fact]
    public async Task EncodingChange_RebindsSessionAndRefreshesSharedState()
    {
        var reader = new EncodingAwareLogReaderService();
        var tailService = new StubFileTailService();
        var detection = new StubEncodingDetectionService
        {
            AutoDetectedEncoding = FileEncoding.Utf8,
            AutoStatusText = "Auto -> UTF-8"
        };
        var registry = new FileSessionRegistry(reader, tailService, detection);
        var tab = CreateTab(reader, tailService, detection, registry, FileEncoding.Auto);

        try
        {
            await tab.LoadAsync();
            var originalSession = tab.ActiveSession;

            Assert.Equal(FileEncoding.Utf8, tab.EffectiveEncoding);
            Assert.Equal(3, tab.TotalLines);

            await ChangeEncodingAndWaitForLoadAsync(tab, FileEncoding.Utf16);

            Assert.NotSame(originalSession, tab.ActiveSession);
            Assert.Equal(FileEncoding.Utf16, tab.Encoding);
            Assert.Equal(FileEncoding.Utf16, tab.EffectiveEncoding);
            Assert.Equal(7, tab.TotalLines);
            Assert.Equal(FileEncoding.Utf16, reader.LastBuildEncoding);
            Assert.Equal(1, registry.ActiveSessionCount);
            Assert.Equal(1, registry.RetainedSessionCount);
            Assert.Equal(0, originalSession.DebugIsDisposed);
        }
        finally
        {
            tab.Dispose();
        }
    }

    [Fact]
    public async Task SharedSession_TailUpdates_RefreshEveryAttachedTab()
    {
        var reader = new SharedTailingLogReaderService(initialLineCount: 3, appendedLineCount: 4);
        var tailService = new StubFileTailService();
        var detection = new StubEncodingDetectionService();
        var registry = new FileSessionRegistry(reader, tailService, detection);
        var tab1 = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8);
        var tab2 = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8);

        try
        {
            await tab1.LoadAsync();
            await tab2.LoadAsync();

            tailService.RaiseLinesAppended(tab1.FilePath);

            await WaitForAsync(() =>
                tab1.StatusText == "4 lines" &&
                tab2.StatusText == "4 lines" &&
                tab1.VisibleLines.LastOrDefault()?.LineNumber == 4 &&
                tab2.VisibleLines.LastOrDefault()?.LineNumber == 4);
        }
        finally
        {
            tab2.Dispose();
            tab1.Dispose();
        }
    }

    [Fact]
    public async Task SharedSession_HiddenTwin_DoesNotSuspendVisibleTwinTailing()
    {
        var reader = new StubLogReaderService();
        var tailService = new StubFileTailService();
        var detection = new StubEncodingDetectionService();
        var registry = new FileSessionRegistry(reader, tailService, detection);
        var visibleTab = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8);
        var hiddenTab = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8);

        try
        {
            await visibleTab.LoadAsync();
            await hiddenTab.LoadAsync();

            hiddenTab.OnBecameHidden();

            Assert.True(visibleTab.IsVisible);
            Assert.False(hiddenTab.IsVisible);
            Assert.False(visibleTab.IsSuspended);
            Assert.Contains(visibleTab.FilePath, tailService.ActiveFiles);
        }
        finally
        {
            hiddenTab.Dispose();
            visibleTab.Dispose();
        }
    }

    [Fact]
    public async Task SharedSession_DisposingLastVisibleTwin_SuspendsWhenOnlyHiddenClientsRemain()
    {
        var reader = new StubLogReaderService();
        var tailService = new StubFileTailService();
        var detection = new StubEncodingDetectionService();
        var registry = new FileSessionRegistry(reader, tailService, detection);
        var visibleTab = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8);
        var hiddenTab = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8);

        try
        {
            await visibleTab.LoadAsync();
            await hiddenTab.LoadAsync();
            hiddenTab.OnBecameHidden();

            visibleTab.Dispose();

            await WaitForAsync(() => hiddenTab.IsSuspended);
            Assert.DoesNotContain(hiddenTab.FilePath, tailService.ActiveFiles);
        }
        finally
        {
            hiddenTab.Dispose();
        }
    }

    [Fact]
    public async Task BeginShutdown_OnOneSharedTab_DoesNotTearDownSessionForOtherTabs()
    {
        var reader = new StubLogReaderService();
        var tailService = new StubFileTailService();
        var detection = new StubEncodingDetectionService();
        var registry = new FileSessionRegistry(reader, tailService, detection);
        var tab1 = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8);
        var tab2 = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8);

        try
        {
            await tab1.LoadAsync();
            await tab2.LoadAsync();

            tab1.StatusText = "Closing";
            tab1.BeginShutdown();

            Assert.Equal(1, registry.ActiveSessionCount);

            tailService.RaiseTailError(tab1.FilePath, "shared failure");

            await WaitForAsync(() => tab2.StatusText == "Tailing stopped: shared failure");

            Assert.Equal("Closing", tab1.StatusText);
            Assert.Equal("Tailing stopped: shared failure", tab2.StatusText);
        }
        finally
        {
            tab2.Dispose();
            tab1.Dispose();
        }
    }

    [Fact]
    public async Task SamePathDifferentEncodings_DisposingOneTab_KeepsVisibleTabTailing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "WeezTailSessionTailTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testRoot);
        var appPathsScope = AppPaths.BeginTestScope(rootPath: testRoot);
        var path = Path.Combine(testRoot, "shared.log");
        await File.WriteAllTextAsync(path, "Line 1\n");

        var reader = new ChunkedLogReaderService();
        using var tailService = new FileTailService();
        var detection = new StubEncodingDetectionService();
        var registry = new FileSessionRegistry(reader, tailService, detection)
        {
            WarmRetentionDuration = TimeSpan.Zero
        };
        var visibleTab = CreateTab(reader, tailService, detection, registry, FileEncoding.Utf8, path);
        var closingTab = CreateTab(reader, tailService, detection, registry, FileEncoding.Ansi, path);

        try
        {
            await visibleTab.LoadAsync();
            await closingTab.LoadAsync();

            Assert.NotSame(visibleTab.ActiveSession, closingTab.ActiveSession);

            closingTab.Dispose();
            await Task.Delay(300);
            await File.AppendAllTextAsync(path, "Line 2\n");

            await WaitForAsync(() =>
                visibleTab.TotalLines == 2 &&
                visibleTab.VisibleLines.LastOrDefault()?.LineNumber == 2 &&
                visibleTab.StatusText == "2 lines");
        }
        finally
        {
            visibleTab.Dispose();
            closingTab.Dispose();
            registry.Dispose();
            appPathsScope.Dispose();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AgentProvider_AlreadyIndexedUiSession_ReusesIndexWithoutAnotherBuild()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"WeezTailUiAgentReuse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        using var appPathsScope = AppPaths.BeginTestScope(rootPath: testRoot);
        var path = Path.Combine(testRoot, "shared.log");
        await File.WriteAllTextAsync(path, "one\ntwo\n");
        var reader = new CountingBoundedLogReaderService();
        var tail = new StubFileTailService();
        var registry = new FileSessionRegistry(reader, tail, new FileEncodingDetectionService());
        using var uiLease = registry.Acquire(path, FileEncoding.Auto);
        await uiLease.Session.LoadAsync(startLoadedTailing: false);
        var existingIndex = uiLease.Session.DebugLineIndex;
        using var provider = new UiIndexedLogSessionProvider(registry);

        using var agentLease = provider.AcquireSession(path);
        var lineCount = await agentLease.UseCurrentIndexAsync(
            (index, _, _) => Task.FromResult(index.LineCount));

        Assert.Equal(2, lineCount);
        Assert.Same(existingIndex, uiLease.Session.DebugLineIndex);
        Assert.Equal(1, reader.UiBuildCount);
        Assert.Equal(0, reader.AgentBuildCount);
        registry.Dispose();
        await (uiLease.Session.DebugLineIndexDisposeTask ?? Task.CompletedTask);
        appPathsScope.Dispose();
        Directory.Delete(testRoot, recursive: true);
    }

    [Fact]
    public async Task AgentProvider_UnopenedFileBuildsBoundedSessionWithoutClientOrTailingAndEvictsOnRelease()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"WeezTailUiAgentCold_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        using var appPathsScope = AppPaths.BeginTestScope(rootPath: testRoot);
        var path = Path.Combine(testRoot, "cold.log");
        await File.WriteAllTextAsync(path, "one\ntwo\n");
        var reader = new CountingBoundedLogReaderService();
        var tail = new StubFileTailService();
        var registry = new FileSessionRegistry(reader, tail, new FileEncodingDetectionService());
        using var provider = new UiIndexedLogSessionProvider(registry);
        var agentLease = Assert.IsType<AgentFileSessionLease>(provider.AcquireSession(path));
        var session = agentLease.DebugSession;

        var lineCount = await agentLease.UseCurrentIndexAsync(
            (index, _, _) => Task.FromResult(index.LineCount));

        Assert.Equal(2, lineCount);
        Assert.Equal(1, reader.AgentBuildCount);
        Assert.Empty(session.GetClientSnapshots());
        Assert.False(session.IsLoading);
        Assert.Empty(tail.ActiveFiles);
        agentLease.Dispose();
        Assert.Equal(0, registry.ActiveSessionCount);
        Assert.Equal(0, registry.RetainedSessionCount);
        Assert.Equal(1, session.DebugIsDisposed);
        await (session.DebugLineIndexDisposeTask ?? Task.CompletedTask);
        registry.Dispose();
        appPathsScope.Dispose();
        Directory.Delete(testRoot, recursive: true);
    }

    [Fact]
    public void AgentProvider_RejectsColdSessionBeyondSeparateCapacity()
    {
        var registry = CreateRegistry();
        using var provider = new UiIndexedLogSessionProvider(
            registry,
            maximumAgentSessions: 1,
            maximumAgentMappedLineOffsets: 100);
        using var first = provider.AcquireSession(@"C:\test\first.log");

        Assert.Throws<IndexedLogSessionCapacityExceededException>(
            () => provider.AcquireSession(@"C:\test\second.log"));

        registry.Dispose();
    }

    [Fact]
    public void AgentLease_DoesNotDisposeUiOwnedSessionAndDoesNotExtendUiWarmRetention()
    {
        var registry = CreateRegistry();
        registry.WarmRetentionDuration = TimeSpan.FromMinutes(2);
        var uiLease = registry.Acquire(@"C:\test\shared.log", FileEncoding.Auto);
        var session = uiLease.Session;
        using var provider = new UiIndexedLogSessionProvider(registry);
        var agentLease = provider.AcquireSession(@"C:\test\shared.log");

        uiLease.Dispose();
        Assert.Equal(0, session.DebugIsDisposed);
        agentLease.Dispose();

        Assert.Equal(1, registry.RetainedSessionCount);
        Assert.Equal(0, session.DebugIsDisposed);
        var disposed = registry.SweepExpiredSessions(DateTime.UtcNow + TimeSpan.FromMinutes(3));
        Assert.Equal(1, disposed);
        Assert.Equal(1, session.DebugIsDisposed);
        registry.Dispose();
    }

    [Fact]
    public async Task UiLoad_PreemptsAgentColdIndexBuildAndPublishesOnlyUiResult()
    {
        var reader = new PreemptibleAgentLogReaderService();
        var registry = new FileSessionRegistry(
            reader,
            new StubFileTailService(),
            new StubEncodingDetectionService());
        using var uiLease = registry.Acquire(@"C:\test\priority.log", FileEncoding.Utf8);
        using var provider = new UiIndexedLogSessionProvider(registry);
        using var agentLease = provider.AcquireSession(@"C:\test\priority.log", FileEncoding.Utf8);
        var agentRead = agentLease.UseCurrentIndexAsync(
            (index, _, _) => Task.FromResult(index.LineCount));
        await reader.AgentBuildStarted.WaitAsync(TimeSpan.FromSeconds(2));

        await uiLease.Session.LoadAsync(startLoadedTailing: false);

        await Assert.ThrowsAsync<IOException>(async () => await agentRead);
        Assert.Equal(1, reader.UiBuildCount);
        Assert.Equal(3, uiLease.Session.DebugLineIndex!.LineCount);
        Assert.False(uiLease.Session.HasLoadError);
        registry.Dispose();
    }

    private static FileSessionRegistry CreateRegistry()
        => new(new StubLogReaderService(), new StubFileTailService(), new StubEncodingDetectionService());

    private static LogTabViewModel CreateTab(
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        FileSessionRegistry registry,
        FileEncoding initialEncoding)
        => CreateTab(logReader, tailService, encodingDetectionService, registry, initialEncoding, @"C:\test\shared.log");

    private static LogTabViewModel CreateTab(
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        FileSessionRegistry registry,
        FileEncoding initialEncoding,
        string filePath)
        => new(
            "test-id",
            filePath,
            logReader,
            tailService,
            encodingDetectionService,
            new AppSettings(),
            skipInitialEncodingResolution: true,
            sessionRegistry: registry,
            initialEncoding: initialEncoding,
            scopeDashboardId: null);

    private static async Task ChangeEncodingAndWaitForLoadAsync(LogTabViewModel tab, FileEncoding encoding)
    {
        tab.Encoding = encoding;
        await WaitForAsync(() => !tab.IsLoading);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
                throw new TimeoutException("Condition was not met within the allotted time.");

            await Task.Delay(25);
        }
    }

    private sealed class EncodingAwareLogReaderService : ILogReaderService
    {
        public FileEncoding LastBuildEncoding { get; private set; } = FileEncoding.Utf8;

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            LastBuildEncoding = encoding;
            return Task.FromResult(CreateIndex(filePath, GetLineCount(encoding)));
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath, GetLineCount(encoding)));

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            var lines = Enumerable.Range(startLine + 1, Math.Max(0, Math.Min(count, index.LineCount - startLine)))
                .Select(lineNumber => $"Line {lineNumber}")
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1}");

        private static int GetLineCount(FileEncoding encoding)
            => encoding switch
            {
                FileEncoding.Utf16 => 7,
                _ => 3
            };

        private static LineIndex CreateIndex(string filePath, int lineCount)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = lineCount * 100
            };

            for (var i = 0; i < lineCount; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    private sealed class SharedTailingLogReaderService : ILogReaderService
    {
        private readonly int _initialLineCount;
        private readonly int _appendedLineCount;

        public SharedTailingLogReaderService(int initialLineCount, int appendedLineCount)
        {
            _initialLineCount = initialLineCount;
            _appendedLineCount = appendedLineCount;
        }

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath, _initialLineCount));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath, _appendedLineCount));

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            var lines = Enumerable.Range(startLine + 1, Math.Max(0, Math.Min(count, index.LineCount - startLine)))
                .Select(lineNumber => $"Line {lineNumber}")
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1}");

        private static LineIndex CreateIndex(string filePath, int lineCount)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = lineCount * 100
            };

            for (var i = 0; i < lineCount; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    private sealed class CountingBoundedLogReaderService : ILogReaderService, IBoundedLogReaderService
    {
        private readonly ChunkedLogReaderService _inner = new();

        public int UiBuildCount { get; private set; }

        public int AgentBuildCount { get; private set; }

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            UiBuildCount++;
            return _inner.BuildIndexAsync(filePath, encoding, ct);
        }

        public Task<LineIndex> BuildBoundedIndexAsync(string filePath, FileEncoding encoding, int maximumLineCount, CancellationToken ct = default)
        {
            AgentBuildCount++;
            return _inner.BuildBoundedIndexAsync(filePath, encoding, maximumLineCount, ct);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => _inner.UpdateIndexAsync(filePath, existingIndex, encoding, ct);

        public Task<LineIndex> UpdateBoundedIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, int maximumLineCount, CancellationToken ct = default)
            => _inner.UpdateBoundedIndexAsync(filePath, existingIndex, encoding, maximumLineCount, ct);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
            => _inner.ReadLinesAsync(filePath, index, startLine, count, encoding, ct);

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => _inner.ReadLineAsync(filePath, index, lineNumber, encoding, ct);

        public Task<IReadOnlyList<BoundedIndexedLine>> ReadBoundedLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            int maximumCharactersPerLine,
            int maximumTotalCharacters,
            CancellationToken ct = default)
            => _inner.ReadBoundedLinesAsync(
                filePath,
                index,
                startLine,
                count,
                encoding,
                maximumCharactersPerLine,
                maximumTotalCharacters,
                ct);
    }

    private sealed class PreemptibleAgentLogReaderService : ILogReaderService, IBoundedLogReaderService
    {
        private readonly TaskCompletionSource _agentBuildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AgentBuildStarted => _agentBuildStarted.Task;

        public int UiBuildCount { get; private set; }

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            UiBuildCount++;
            return Task.FromResult(CreateIndex(filePath, 3));
        }

        public async Task<LineIndex> BuildBoundedIndexAsync(string filePath, FileEncoding encoding, int maximumLineCount, CancellationToken ct = default)
        {
            _agentBuildStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return CreateIndex(filePath, 1);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<LineIndex> UpdateBoundedIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, int maximumLineCount, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<BoundedIndexedLine>> ReadBoundedLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            int maximumCharactersPerLine,
            int maximumTotalCharacters,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BoundedIndexedLine>>([]);

        private static LineIndex CreateIndex(string filePath, int lineCount)
        {
            var index = new LineIndex { FilePath = filePath, FileSize = lineCount * 10 };
            for (var line = 0; line < lineCount; line++)
                index.LineOffsets.Add(line * 10L);
            return index;
        }
    }
}
