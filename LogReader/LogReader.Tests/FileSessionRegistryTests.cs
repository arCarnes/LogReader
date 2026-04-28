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
        var testRoot = Path.Combine(Path.GetTempPath(), "LogReaderSessionTailTests_" + Guid.NewGuid().ToString("N")[..8]);
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
}
