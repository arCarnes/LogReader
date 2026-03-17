using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

namespace LogReader.Tests;

public class MainViewModelTests
{
    // ─── Stubs (test-specific — shared stubs are in Stubs.cs) ────────────────

    private class StubSessionRepository : ISessionRepository
    {
        public SessionState State { get; set; } = new();
        public Task<SessionState> LoadAsync() => Task.FromResult(State);
        public Task SaveAsync(SessionState state) { State = state; return Task.CompletedTask; }
    }

    private class RecordingSearchService : ISearchService
    {
        public SearchResult NextResult { get; set; } = new();
        public int SearchFileCallCount { get; private set; }
        public SearchRequest? LastSearchFileRequest { get; private set; }

        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        {
            SearchFileCallCount++;
            LastSearchFileRequest = new SearchRequest
            {
                Query = request.Query,
                IsRegex = request.IsRegex,
                CaseSensitive = request.CaseSensitive,
                WholeWord = request.WholeWord,
                FilePaths = request.FilePaths.ToList(),
                StartLineNumber = request.StartLineNumber,
                EndLineNumber = request.EndLineNumber,
                FromTimestamp = request.FromTimestamp,
                ToTimestamp = request.ToTimestamp
            };
            return Task.FromResult(new SearchResult
            {
                FilePath = NextResult.FilePath,
                Hits = NextResult.Hits.ToList(),
                Error = NextResult.Error,
                HasParseableTimestamps = NextResult.HasParseableTimestamps
            });
        }

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }

    private sealed class YieldingSessionRepository : ISessionRepository
    {
        public SessionState State { get; set; } = new();

        public async Task<SessionState> LoadAsync()
        {
            await Task.Yield();
            return State;
        }

        public async Task SaveAsync(SessionState state)
        {
            await Task.Yield();
            State = state;
        }
    }

    private sealed class YieldingLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries;

        public YieldingLogFileRepository(IEnumerable<LogFileEntry>? entries = null)
        {
            _entries = entries?.ToList() ?? new List<LogFileEntry>();
        }

        public async Task<List<LogFileEntry>> GetAllAsync()
        {
            await Task.Yield();
            return _entries.ToList();
        }

        public async Task<LogFileEntry?> GetByIdAsync(string id)
        {
            await Task.Yield();
            return _entries.FirstOrDefault(entry => entry.Id == id);
        }

        public async Task<LogFileEntry?> GetByPathAsync(string filePath)
        {
            await Task.Yield();
            return _entries.FirstOrDefault(entry =>
                string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }

        public async Task AddAsync(LogFileEntry entry)
        {
            await Task.Yield();
            _entries.Add(entry);
        }

        public async Task UpdateAsync(LogFileEntry entry)
        {
            await Task.Yield();
            var index = _entries.FindIndex(existing => existing.Id == entry.Id);
            if (index >= 0)
                _entries[index] = entry;
        }

        public async Task DeleteAsync(string id)
        {
            await Task.Yield();
            _entries.RemoveAll(entry => entry.Id == id);
        }
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private Func<Task>? _asyncAction;

        private SingleThreadSynchronizationContext()
        {
            _thread = new Thread(RunOnCurrentThread)
            {
                IsBackground = true,
                Name = nameof(SingleThreadSynchronizationContext)
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public static async Task RunAsync(Func<Task> asyncAction)
        {
            using var context = new SingleThreadSynchronizationContext
            {
                _asyncAction = asyncAction
            };
            context._thread.Start();
            await context._completion.Task;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == _thread)
            {
                d(state);
                return;
            }

            using var signal = new ManualResetEventSlim();
            Exception? exception = null;
            Post(_ =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    signal.Set();
                }
            }, null);

            signal.Wait();
            if (exception != null)
                ExceptionDispatchInfo.Capture(exception).Throw();
        }

        public void Dispose()
        {
            CompleteQueue();
            if (_thread.IsAlive)
                _thread.Join(TimeSpan.FromSeconds(5));
        }

        private void RunOnCurrentThread()
        {
            var previousContext = Current;
            SetSynchronizationContext(this);
            try
            {
                Task asyncTask;
                try
                {
                    asyncTask = _asyncAction!();
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                    return;
                }

                asyncTask.ContinueWith(
                    static (task, state) =>
                    {
                        var context = (SingleThreadSynchronizationContext)state!;
                        if (task.IsFaulted)
                            context._completion.TrySetException(task.Exception!.InnerExceptions);
                        else if (task.IsCanceled)
                            context._completion.TrySetCanceled();
                        else
                            context._completion.TrySetResult();

                        context.CompleteQueue();
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);

                foreach (var workItem in _queue.GetConsumingEnumerable())
                    workItem.Callback(workItem.State);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                CompleteQueue();
            }
            finally
            {
                SetSynchronizationContext(previousContext);
            }
        }

        private void CompleteQueue()
        {
            if (!_queue.IsAddingCompleted)
                _queue.CompleteAdding();
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private MainViewModel CreateViewModel(
        ILogFileRepository? fileRepo = null,
        ILogGroupRepository? groupRepo = null,
        ISessionRepository? sessionRepo = null,
        ISettingsRepository? settingsRepo = null,
        IFileTailService? tailService = null,
        ILogReaderService? logReader = null,
        ISearchService? searchService = null,
        IEncodingDetectionService? encodingDetectionService = null,
        ILogTimestampNavigationService? timestampNavigationService = null)
    {
        return new MainViewModel(
            fileRepo ?? new StubLogFileRepository(),
            groupRepo ?? new StubLogGroupRepository(),
            sessionRepo ?? new StubSessionRepository(),
            settingsRepo ?? new StubSettingsRepository(),
            logReader ?? new StubLogReaderService(),
            searchService ?? new StubSearchService(),
            tailService ?? new StubFileTailService(),
            encodingDetectionService ?? new FileEncodingDetectionService(),
            timestampNavigationService ?? new LogTimestampNavigationService(),
            enableLifecycleTimer: false);
    }

    private static Dictionary<string, long> GetTabOrderMap(MainViewModel vm, string fieldName)
    {
        var field = typeof(MainViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (Dictionary<string, long>)field!.GetValue(vm)!;
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenFilePathAsync_DeduplicatesByPath()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\file.log");
        await vm.OpenFilePathAsync(@"C:\test\file.log");

        Assert.Single(vm.Tabs);
    }

    [Fact]
    public async Task OpenFilePathAsync_RaisesTabCollectionChangedOnCallingSynchronizationContext()
    {
        var fileRepo = new YieldingLogFileRepository();
        var collectionChangedThreads = new ConcurrentBag<int>();
        var originThreadId = -1;

        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            originThreadId = Environment.CurrentManagedThreadId;

            var vm = CreateViewModel(fileRepo: fileRepo);
            await vm.InitializeAsync();

            vm.Tabs.CollectionChanged += (_, _) => collectionChangedThreads.Add(Environment.CurrentManagedThreadId);

            await vm.OpenFilePathAsync(@"C:\test\file.log");

            Assert.Single(vm.Tabs);
        });

        var eventThreads = collectionChangedThreads.ToArray();
        Assert.NotEmpty(eventThreads);
        Assert.All(eventThreads, threadId => Assert.Equal(originThreadId, threadId));
    }

    [Fact]
    public async Task InitializeAsync_RestoresSelectedTabOnCallingSynchronizationContext()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-restore-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "line one\nline two\n");

        try
        {
            var entry = new LogFileEntry
            {
                Id = Guid.NewGuid().ToString(),
                FilePath = path
            };
            var fileRepo = new YieldingLogFileRepository(new[] { entry });
            var sessionRepo = new YieldingSessionRepository
            {
                State = new SessionState
                {
                    ActiveTabId = entry.Id,
                    OpenTabs =
                    [
                        new OpenTabState
                        {
                            FileId = entry.Id,
                            FilePath = path,
                            Encoding = FileEncoding.Utf8,
                            AutoScrollEnabled = true,
                            IsPinned = false
                        }
                    ]
                }
            };
            var selectedTabThreads = new ConcurrentBag<int>();
            var originThreadId = -1;

            await SingleThreadSynchronizationContext.RunAsync(async () =>
            {
                originThreadId = Environment.CurrentManagedThreadId;

                var vm = CreateViewModel(fileRepo: fileRepo, sessionRepo: sessionRepo);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.SelectedTab))
                        selectedTabThreads.Add(Environment.CurrentManagedThreadId);
                };

                await vm.InitializeAsync();

                Assert.NotNull(vm.SelectedTab);
                Assert.Equal(path, vm.SelectedTab!.FilePath);
            });

            var eventThreads = selectedTabThreads.ToArray();
            Assert.NotEmpty(eventThreads);
            Assert.All(eventThreads, threadId => Assert.Equal(originThreadId, threadId));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task InitializeAsync_DoesNotSeedRootBranch_WhenNoGroups()
    {
        var vm = new MainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSessionRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new LogTimestampNavigationService(),
            enableLifecycleTimer: false);

        await vm.InitializeAsync();

        Assert.Empty(vm.Groups);
    }

    [Fact]
    public async Task OpenFilePathAsync_CaseInsensitiveDedupe()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\file.log");
        await vm.OpenFilePathAsync(@"C:\TEST\FILE.LOG");

        Assert.Single(vm.Tabs);
    }

    [Fact]
    public async Task OpenFilePathAsync_DefaultsToAutoEncodingSelection()
    {
        var reader = new StubLogReaderService();
        var vm = CreateViewModel(logReader: reader);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\file.log");

        Assert.Single(vm.Tabs);
        Assert.Equal(FileEncoding.Auto, vm.Tabs[0].Encoding);
        Assert.Equal(FileEncoding.Utf8, vm.Tabs[0].EffectiveEncoding);
        Assert.Equal(FileEncoding.Utf8, reader.LastBuildEncoding);
    }

    [Fact]
    public async Task OpenFilePathAsync_WhenPrimaryEncodingFails_DoesNotFallback()
    {
        var reader = new StubLogReaderService();
        reader.BuildFailures.Add(FileEncoding.Utf8);
        var vm = CreateViewModel(logReader: reader);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\file.log");

        Assert.Single(vm.Tabs);
        Assert.Equal(FileEncoding.Auto, vm.Tabs[0].Encoding);
        Assert.True(vm.Tabs[0].HasLoadError);
        Assert.Equal(new[] { FileEncoding.Utf8 }, reader.AttemptedBuildEncodings);
    }

    [Fact]
    public async Task OpenFilePathAsync_AutoDetectsUtf8Bom_WhenFileHasUtf8Bom()
    {
        var reader = new StubLogReaderService();
        var vm = CreateViewModel(logReader: reader);
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-utf8bom-{Guid.NewGuid():N}.log");
        try
        {
            var bytes = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes("line one\nline two\n"))
                .ToArray();
            await File.WriteAllBytesAsync(path, bytes);

            await vm.OpenFilePathAsync(path);

            Assert.Single(vm.Tabs);
            Assert.Equal(FileEncoding.Auto, vm.Tabs[0].Encoding);
            Assert.Equal(FileEncoding.Utf8Bom, vm.Tabs[0].EffectiveEncoding);
            Assert.Equal(FileEncoding.Utf8Bom, reader.LastBuildEncoding);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenFilePathAsync_AutoDetectsUtf16_WhenFileLooksLikeUtf16LeWithoutBom()
    {
        var reader = new StubLogReaderService();
        var vm = CreateViewModel(logReader: reader);
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-utf16le-{Guid.NewGuid():N}.log");
        try
        {
            var bytes = Encoding.Unicode.GetBytes("line one\nline two\n");
            await File.WriteAllBytesAsync(path, bytes);

            await vm.OpenFilePathAsync(path);

            Assert.Single(vm.Tabs);
            Assert.Equal(FileEncoding.Auto, vm.Tabs[0].Encoding);
            Assert.Equal(FileEncoding.Utf16, vm.Tabs[0].EffectiveEncoding);
            Assert.Equal(FileEncoding.Utf16, reader.LastBuildEncoding);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenFilePathAsync_FallsBackToUtf8_WhenDetectionIsAmbiguous()
    {
        var reader = new StubLogReaderService();
        var vm = CreateViewModel(logReader: reader);
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-ascii-{Guid.NewGuid():N}.log");
        try
        {
            var bytes = Encoding.ASCII.GetBytes("line one\nline two\n");
            await File.WriteAllBytesAsync(path, bytes);

            await vm.OpenFilePathAsync(path);

            Assert.Single(vm.Tabs);
            Assert.Equal(FileEncoding.Auto, vm.Tabs[0].Encoding);
            Assert.Equal(FileEncoding.Utf8, vm.Tabs[0].EffectiveEncoding);
            Assert.Contains("fallback", vm.Tabs[0].EncodingStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(FileEncoding.Utf8, reader.LastBuildEncoding);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task CloseTab_DisposesAndRemovesTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\file.log");

        var tab = vm.Tabs[0];
        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public async Task CloseTab_RemovesTabOrderingMetadata()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\file.log");

        var tab = vm.Tabs[0];
        vm.TogglePinTab(tab);

        var openOrder = GetTabOrderMap(vm, "_tabOpenOrder");
        var pinOrder = GetTabOrderMap(vm, "_tabPinOrder");
        Assert.Contains(tab.FileId, openOrder.Keys);
        Assert.Contains(tab.FileId, pinOrder.Keys);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.DoesNotContain(tab.FileId, openOrder.Keys);
        Assert.DoesNotContain(tab.FileId, pinOrder.Keys);
    }

    [Fact]
    public async Task FilteredTabs_NoDashboardActive_ReturnsOnlyAdHocTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Single(filtered);
        Assert.Equal(vm.Tabs[1].FilePath, filtered[0].FilePath);
    }

    [Fact]
    public async Task FilteredTabs_FiltersWhenDashboardIsActive()
    {
        var groupRepo = new StubLogGroupRepository();
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        // Create a group containing only the first file
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];
        group.Model.FileIds.Add(vm.Tabs[0].FileId);

        // Select the group to enable filtering
        vm.ToggleGroupSelection(group);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Single(filtered);
        Assert.Equal(vm.Tabs[0].FilePath, filtered[0].FilePath);
    }

    [Fact]
    public async Task NavigateToLineAsync_WhenHitIsInDifferentDashboard_SwitchesActiveDashboardAndTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        g1.Model.FileIds.Add(tabA.FileId);
        g2.Model.FileIds.Add(tabB.FileId);

        vm.ToggleGroupSelection(g1);
        Assert.Equal(g1.Id, vm.ActiveDashboardId);
        Assert.DoesNotContain(tabB, vm.FilteredTabs);

        await vm.NavigateToLineAsync(tabB.FilePath, 42);

        Assert.Equal(g2.Id, vm.ActiveDashboardId);
        Assert.True(g2.IsSelected);
        Assert.False(g1.IsSelected);
        Assert.Same(tabB, vm.SelectedTab);
        Assert.Single(vm.FilteredTabs);
        Assert.Contains(tabB, vm.FilteredTabs);
        Assert.Equal(42, tabB.NavigateToLineNumber);
    }

    [Fact]
    public async Task NavigateToLineAsync_WhenHitIsAdHoc_ClearsActiveDashboardAndSelectsTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        dashboard.Model.FileIds.Add(tabA.FileId);

        vm.ToggleGroupSelection(dashboard);
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.DoesNotContain(tabB, vm.FilteredTabs);

        await vm.NavigateToLineAsync(tabB.FilePath, 77);

        Assert.Null(vm.ActiveDashboardId);
        Assert.All(vm.Groups, g => Assert.False(g.IsSelected));
        Assert.Same(tabB, vm.SelectedTab);
        Assert.Single(vm.FilteredTabs);
        Assert.Contains(tabB, vm.FilteredTabs);
        Assert.Equal(77, tabB.NavigateToLineNumber);
    }

    [Fact]
    public async Task NavigateToTimestampAsync_InvalidInput_ReturnsValidationMessage()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        var previousStatus = vm.SelectedTab!.StatusText;

        var status = await vm.NavigateToTimestampAsync("not a timestamp");

        Assert.Equal("Invalid timestamp. Use ISO-8601, yyyy-MM-dd HH:mm:ss, or HH:mm:ss.fff.", status);
        Assert.NotNull(vm.SelectedTab);
        Assert.Equal(previousStatus, vm.SelectedTab!.StatusText);
    }

    [Fact]
    public async Task NavigateToTimestampAsync_ExactMatch_NavigatesToMatchingLine()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "2026-03-09 19:49:10 INFO one\n2026-03-09 19:49:20 INFO two\n2026-03-09 19:49:30 INFO three\n");
            await vm.OpenFilePathAsync(path);

            var status = await vm.NavigateToTimestampAsync("2026-03-09 19:49:20");

            Assert.Contains("exact timestamp match", status, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(vm.SelectedTab);
            Assert.Equal(2, vm.SelectedTab!.NavigateToLineNumber);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task NavigateToTimestampAsync_NoExactMatch_NavigatesToNearestLine()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "2026-03-09 19:49:10 INFO one\n2026-03-09 19:49:30 INFO two\n");
            await vm.OpenFilePathAsync(path);

            var status = await vm.NavigateToTimestampAsync("2026-03-09 19:49:26");

            Assert.Contains("no exact timestamp match", status, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(vm.SelectedTab);
            Assert.Equal(2, vm.SelectedTab!.NavigateToLineNumber);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task NavigateToTimestampAsync_NoParseableTimestamps_ReturnsClearStatus()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path, "INFO one\nWARN two\nERROR three\n");
            await vm.OpenFilePathAsync(path);

            var status = await vm.NavigateToTimestampAsync("2026-03-09 19:49:26");

            Assert.Equal("No parseable timestamps found in the current file.", status);
            Assert.NotNull(vm.SelectedTab);
            Assert.Equal("No parseable timestamps found in the current file.", vm.SelectedTab!.StatusText);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task NavigateToLineAsync_StringInput_NavigatesToRequestedLine()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\line-target.log");

        var status = await vm.NavigateToLineAsync("42");

        Assert.Equal("Navigated to line 42.", status);
        Assert.NotNull(vm.SelectedTab);
        Assert.Equal(42, vm.SelectedTab!.NavigateToLineNumber);
        Assert.Equal("Navigated to line 42.", vm.SelectedTab.StatusText);
    }

    [Fact]
    public async Task NavigateToLineAsync_StringInput_InvalidValue_ReturnsValidationMessage()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\line-target.log");

        var status = await vm.NavigateToLineAsync("abc");

        Assert.Equal("Invalid line number. Enter a whole number greater than 0.", status);
        Assert.NotNull(vm.SelectedTab);
        Assert.NotEqual(0, vm.SelectedTab!.TotalLines);
        Assert.Equal(-1, vm.SelectedTab.NavigateToLineNumber);
    }

    [Fact]
    public async Task FilterPanel_ApplyFilter_CurrentTabOnly_ActivatesSnapshotFilter()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");
        Assert.NotNull(vm.SelectedTab);

        search.NextResult = new SearchResult
        {
            FilePath = vm.SelectedTab!.FilePath,
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 2, LineText = "Line 2", MatchStart = 0, MatchLength = 4 },
                new() { LineNumber = 5, LineText = "Line 5", MatchStart = 0, MatchLength = 4 }
            },
            HasParseableTimestamps = true
        };

        vm.FilterPanel.Query = "Line";
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.True(vm.SelectedTab.IsFilterActive);
        Assert.Equal(2, vm.SelectedTab.FilteredLineCount);
        Assert.Equal(2, vm.SelectedTab.VisibleLines.Count);
        Assert.Equal(new[] { 2, 5 }, vm.SelectedTab.VisibleLines.Select(l => l.LineNumber).ToArray());
        Assert.Equal("Filter active: 2 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_ClearFilter_RestoresFullSnapshotView()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");
        Assert.NotNull(vm.SelectedTab);

        search.NextResult = new SearchResult
        {
            FilePath = vm.SelectedTab!.FilePath,
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 3, LineText = "Line 3", MatchStart = 0, MatchLength = 4 }
            },
            HasParseableTimestamps = true
        };

        vm.FilterPanel.Query = "Line";
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
        Assert.True(vm.SelectedTab.IsFilterActive);

        await vm.FilterPanel.ClearFilterCommand.ExecuteAsync(null);

        Assert.False(vm.SelectedTab.IsFilterActive);
        Assert.Equal(vm.SelectedTab.TotalLines, vm.SelectedTab.DisplayLineCount);
        Assert.Equal("Filter cleared.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_ApplyFilter_InvalidTimestampRange_DoesNotSearch()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");

        vm.FilterPanel.Query = "Line";
        vm.FilterPanel.FromTimestamp = "invalid";

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(0, search.SearchFileCallCount);
        Assert.Contains("Invalid 'From' timestamp", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task CloseAllTabs_ClearsAllTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CloseAllTabsAsync();

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public async Task CloseAllTabs_ClearsActiveDashboardSelection()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        vm.ToggleGroupSelection(dashboard);
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.True(dashboard.IsSelected);

        await vm.CloseAllTabsAsync();

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.ActiveDashboardId);
        Assert.All(vm.Groups, g => Assert.False(g.IsSelected));
    }

    [Fact]
    public async Task CloseOtherTabs_KeepsOnlySpecifiedTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        var keepTab = vm.Tabs[1];
        await vm.CloseOtherTabsAsync(keepTab);

        Assert.Single(vm.Tabs);
        Assert.Same(keepTab, vm.Tabs[0]);
        Assert.Same(keepTab, vm.SelectedTab);
    }

    [Fact]
    public async Task CloseAllButPinned_KeepsPinnedTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.Tabs[0].IsPinned = true;
        vm.Tabs[2].IsPinned = true;

        await vm.CloseAllButPinnedAsync();

        Assert.Equal(2, vm.Tabs.Count);
        Assert.All(vm.Tabs, t => Assert.True(t.IsPinned));
    }

    [Fact]
    public async Task TogglePinTab_TogglesIsPinned()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        var tab = vm.Tabs[0];
        Assert.False(tab.IsPinned);

        vm.TogglePinTab(tab);
        Assert.True(tab.IsPinned);

        vm.TogglePinTab(tab);
        Assert.False(tab.IsPinned);
    }

    [Fact]
    public async Task FilteredTabs_SortsPinnedFirst()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        // Pin the last tab
        vm.Tabs[2].IsPinned = true;

        var filtered = vm.FilteredTabs.ToList();
        Assert.True(filtered[0].IsPinned);
        Assert.Equal(@"C:\test\c.log", filtered[0].FilePath);
    }

    [Fact]
    public async Task FilteredTabs_UsesPinnedAndUnpinnedLanes()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");
        await vm.OpenFilePathAsync(@"C:\test\d.log");

        vm.TogglePinTab(vm.Tabs[2]); // c pinned first
        vm.TogglePinTab(vm.Tabs[0]); // a pinned second

        var ordered = vm.FilteredTabs.Select(t => t.FilePath).ToList();
        Assert.Equal(
            new[] { @"C:\test\c.log", @"C:\test\a.log", @"C:\test\b.log", @"C:\test\d.log" },
            ordered);
    }

    [Fact]
    public async Task FilteredTabs_UnpinnedOrder_RemainsStableAcrossSelectionChanges()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        var initialUnpinnedOrder = vm.FilteredTabs
            .Where(t => !t.IsPinned)
            .Select(t => t.FilePath)
            .ToList();

        vm.SelectedTab = vm.Tabs[0];
        vm.SelectedTab = vm.Tabs[2];

        var afterSelectionChange = vm.FilteredTabs
            .Where(t => !t.IsPinned)
            .Select(t => t.FilePath)
            .ToList();

        Assert.Equal(initialUnpinnedOrder, afterSelectionChange);
    }

    [Fact]
    public async Task FilteredTabs_PinnedOrder_RemainsStableAcrossSelectionChanges()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.TogglePinTab(vm.Tabs[1]); // b pinned first
        vm.TogglePinTab(vm.Tabs[0]); // a pinned second

        var initialPinnedOrder = vm.FilteredTabs
            .Where(t => t.IsPinned)
            .Select(t => t.FilePath)
            .ToList();

        vm.SelectedTab = vm.Tabs[2];
        var afterSelectionChange = vm.FilteredTabs
            .Where(t => t.IsPinned)
            .Select(t => t.FilePath)
            .ToList();

        Assert.Equal(initialPinnedOrder, afterSelectionChange);
    }

    [Fact]
    public async Task TogglePinTab_RePinningTab_UpdatesPinnedOrderDeterministically()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.TogglePinTab(vm.Tabs[0]); // a
        vm.TogglePinTab(vm.Tabs[1]); // b
        vm.TogglePinTab(vm.Tabs[0]); // unpin a
        vm.TogglePinTab(vm.Tabs[0]); // re-pin a, should become after b

        var pinned = vm.FilteredTabs.Where(t => t.IsPinned).Select(t => t.FilePath).ToList();
        Assert.Equal(new[] { @"C:\test\b.log", @"C:\test\a.log" }, pinned);
    }

    [Fact]
    public async Task SelectNextTabCommand_SelectsNextTabInFilteredOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.SelectedTab = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        vm.SelectNextTabCommand.Execute(null);

        Assert.Equal(@"C:\test\b.log", vm.SelectedTab!.FilePath);
    }

    [Fact]
    public async Task SelectPreviousTabCommand_SelectsPreviousTabInFilteredOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.SelectedTab = vm.Tabs.First(t => t.FilePath == @"C:\test\c.log");
        vm.SelectPreviousTabCommand.Execute(null);

        Assert.Equal(@"C:\test\b.log", vm.SelectedTab!.FilePath);
    }

    [Fact]
    public async Task SelectPreviousTabCommand_WhenNoSelectedTab_SelectsLastTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        vm.SelectedTab = null;
        vm.SelectPreviousTabCommand.Execute(null);

        Assert.Equal(@"C:\test\b.log", vm.SelectedTab!.FilePath);
    }

    [Fact]
    public async Task SessionPersistence_PreservesIsPinned()
    {
        var sessionRepo = new StubSessionRepository();
        var vm = CreateViewModel(sessionRepo: sessionRepo);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        vm.Tabs[0].IsPinned = true;
        await vm.SaveSessionAsync();

        Assert.True(sessionRepo.State.OpenTabs[0].IsPinned);
    }

    [Fact]
    public async Task GlobalAutoScrollEnabled_UpdatesAllOpenTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        vm.GlobalAutoScrollEnabled = false;

        Assert.All(vm.Tabs, tab => Assert.False(tab.AutoScrollEnabled));
    }

    [Fact]
    public async Task ApplySelectedTabEncodingToAllCommand_AppliesEncodingAcrossTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.SelectedTab = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        vm.SelectedTab!.Encoding = FileEncoding.Utf16;

        await vm.ApplySelectedTabEncodingToAllCommand.ExecuteAsync(null);

        Assert.All(vm.Tabs, tab => Assert.Equal(FileEncoding.Utf16, tab.Encoding));
    }

    // ─── Group operation tests (#8) ───────────────────────────────────────────

    [Fact]
    public async Task MoveGroupUpAsync_MovesGroupUp()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var firstId = vm.Groups[0].Id;
        var secondId = vm.Groups[1].Id;

        await vm.MoveGroupUpAsync(vm.Groups[1]);

        Assert.Equal(secondId, vm.Groups[0].Id);
        Assert.Equal(firstId, vm.Groups[1].Id);
    }

    [Fact]
    public async Task MoveGroupUpAsync_AlreadyFirst_DoesNothing()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var first = vm.Groups[0];
        var second = vm.Groups[1];

        await vm.MoveGroupUpAsync(first);

        Assert.Same(first, vm.Groups[0]);
        Assert.Same(second, vm.Groups[1]);
    }

    [Fact]
    public async Task MoveGroupDownAsync_MovesGroupDown()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var firstId = vm.Groups[0].Id;
        var secondId = vm.Groups[1].Id;

        await vm.MoveGroupDownAsync(vm.Groups[0]);

        Assert.Equal(secondId, vm.Groups[0].Id);
        Assert.Equal(firstId, vm.Groups[1].Id);
    }

    [Fact]
    public async Task MoveGroupDownAsync_AlreadyLast_DoesNothing()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var first = vm.Groups[0];
        var last = vm.Groups[1];

        await vm.MoveGroupDownAsync(last);

        Assert.Same(first, vm.Groups[0]);
        Assert.Same(last, vm.Groups[1]);
    }

    [Fact]
    public async Task ToggleGroupSelection_SingleSelect_ClearsOthers()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];

        vm.ToggleGroupSelection(g1);
        Assert.True(g1.IsSelected);
        Assert.False(g2.IsSelected);

        vm.ToggleGroupSelection(g2); // single-select: clears g1
        Assert.False(g1.IsSelected);
        Assert.True(g2.IsSelected);
    }

    [Fact]
    public async Task ToggleGroupSelection_MultiSelectFlag_IsIgnored_ForSingleActiveDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];

        vm.ToggleGroupSelection(g1);
        vm.ToggleGroupSelection(g2, isMultiSelect: true);

        Assert.False(g1.IsSelected);
        Assert.True(g2.IsSelected);
        Assert.Equal(g2.Id, vm.ActiveDashboardId);
    }

    [Fact]
    public async Task OpenDashboardFilesAsync_SkipsMissingFiles()
    {
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];

        // Add a file entry whose path doesn't exist on disk
        var missing = new LogReader.Core.Models.LogFileEntry { FilePath = @"C:\does-not-exist-logread-test.log" };
        await fileRepo.AddAsync(missing);
        group.Model.FileIds.Add(missing.Id);

        await vm.OpenGroupFilesAsync(group);

        Assert.Empty(vm.Tabs); // missing file must not open a tab
    }

    [Fact]
    public async Task OpenGroupFilesAsync_OpensFilesInDashboardFileOrder()
    {
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];

        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmOrder_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);
        try
        {
            var bPath = Path.Combine(testDir, "b.log");
            var aPath = Path.Combine(testDir, "a.log");
            var cPath = Path.Combine(testDir, "c.log");
            await File.WriteAllTextAsync(bPath, "b");
            await File.WriteAllTextAsync(aPath, "a");
            await File.WriteAllTextAsync(cPath, "c");

            var entryB = new LogFileEntry { FilePath = bPath };
            var entryA = new LogFileEntry { FilePath = aPath };
            var entryC = new LogFileEntry { FilePath = cPath };
            await fileRepo.AddAsync(entryB);
            await fileRepo.AddAsync(entryA);
            await fileRepo.AddAsync(entryC);

            group.Model.FileIds.Add(entryB.Id);
            group.Model.FileIds.Add(entryA.Id);
            group.Model.FileIds.Add(entryC.Id);

            await vm.OpenGroupFilesAsync(group);

            var openedPaths = vm.Tabs.Select(t => t.FilePath).ToList();
            Assert.Equal(new[] { bPath, aPath, cPath }, openedPaths);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task DashboardFilter_HidesTabs_StopsTailingForHiddenTabs()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        vm.ToggleGroupSelection(g1);

        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        Assert.True(tabA.IsVisible);
        Assert.False(tabA.IsSuspended);
        Assert.False(tabB.IsVisible);
        Assert.True(tabB.IsSuspended);
        Assert.DoesNotContain(@"C:\test\b.log", tailService.ActiveFiles);
    }

    [Fact]
    public async Task SelectedTabChange_VisibleBackgroundTabsRemainTailedAtBackgroundRate()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await Task.Delay(25);

        Assert.Equal(@"C:\test\b.log", vm.SelectedTab!.FilePath);
        Assert.Contains(@"C:\test\b.log", tailService.ActiveFiles);
        Assert.Contains(@"C:\test\a.log", tailService.ActiveFiles);
        Assert.Equal(250, tailService.PollingByFile[@"C:\test\b.log"]);
        Assert.Equal(2000, tailService.PollingByFile[@"C:\test\a.log"]);
    }

    [Fact]
    public async Task SelectedTabChange_SwapsActiveAndBackgroundPollingRates()
    {
        var tailService = new StubFileTailService();
        var reader = new StubLogReaderService();
        var vm = CreateViewModel(tailService: tailService, logReader: reader);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        vm.SelectedTab = tabA;
        await Task.Delay(25);

        Assert.Equal(0, reader.UpdateIndexCallCount);
        Assert.Contains(@"C:\test\a.log", tailService.ActiveFiles);
        Assert.Contains(@"C:\test\b.log", tailService.ActiveFiles);
        Assert.Equal(250, tailService.PollingByFile[@"C:\test\a.log"]);
        Assert.Equal(2000, tailService.PollingByFile[@"C:\test\b.log"]);
    }

    [Fact]
    public async Task HiddenTab_BecomesVisible_ResumesTailing()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        vm.ToggleGroupSelection(g1); // hides tab b
        vm.ToggleGroupSelection(g2); // shows tab b

        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        Assert.True(tabB.IsVisible);
        Assert.False(tabB.IsSuspended);
        Assert.Contains(@"C:\test\b.log", tailService.ActiveFiles);
    }

    [Fact]
    public async Task LifecycleMaintenance_PurgesOldHiddenTabs_ButKeepsPinned()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();
        vm.HiddenTabPurgeAfter = TimeSpan.Zero;

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        tabA.IsPinned = true;

        vm.ToggleGroupSelection(g2); // hides tabA
        vm.RunTabLifecycleMaintenance();

        Assert.Equal(2, vm.Tabs.Count); // pinned tab is preserved

        tabA.IsPinned = false;
        vm.RunTabLifecycleMaintenance();

        Assert.Single(vm.Tabs);
        Assert.Equal(@"C:\test\b.log", vm.Tabs[0].FilePath);
    }

    [Fact]
    public async Task LifecycleMaintenance_Purge_UpdatesSavedSessionState()
    {
        var sessionRepo = new StubSessionRepository();
        var vm = CreateViewModel(sessionRepo: sessionRepo);
        await vm.InitializeAsync();
        vm.HiddenTabPurgeAfter = TimeSpan.Zero;

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        vm.ToggleGroupSelection(g2); // hides tab a
        vm.RunTabLifecycleMaintenance();
        await Task.Delay(25); // SaveSessionAsync is fire-and-forget from maintenance

        Assert.Single(vm.Tabs);
        Assert.Single(sessionRepo.State.OpenTabs);
        Assert.Equal(@"C:\test\b.log", sessionRepo.State.OpenTabs[0].FilePath);
    }

    [Fact]
    public async Task LifecycleMaintenance_Purge_RemovesTabOrderingMetadata()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.HiddenTabPurgeAfter = TimeSpan.Zero;

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        var tabAId = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log").FileId;
        vm.ToggleGroupSelection(g2); // hides tab a

        var openOrder = GetTabOrderMap(vm, "_tabOpenOrder");
        var pinOrder = GetTabOrderMap(vm, "_tabPinOrder");
        Assert.Contains(tabAId, openOrder.Keys);

        vm.RunTabLifecycleMaintenance();

        Assert.DoesNotContain(tabAId, openOrder.Keys);
        Assert.DoesNotContain(tabAId, pinOrder.Keys);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WhenLifecycleTimerEnabled()
    {
        var vm = new MainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSessionRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new LogTimestampNavigationService(),
            enableLifecycleTimer: true);

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_DisposesOpenTabsAndStopsTailing()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        Assert.Contains(@"C:\test\a.log", tailService.ActiveFiles);
        Assert.Contains(@"C:\test\b.log", tailService.ActiveFiles);

        vm.Dispose();

        Assert.Empty(tailService.ActiveFiles);
        Assert.Contains(@"C:\test\a.log", tailService.StoppedFiles);
        Assert.Contains(@"C:\test\b.log", tailService.StoppedFiles);
    }

    [Fact]
    public async Task RebuildGroupsCollection_DetachesOldGroupPropertyChangedHandlers()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var originalGroup = vm.Groups[0];

        Assert.Equal(1, TestHelpers.GetPropertyChangedSubscriberCount(originalGroup));

        await vm.CreateGroupCommand.ExecuteAsync(null);

        Assert.Equal(0, TestHelpers.GetPropertyChangedSubscriberCount(originalGroup));
    }

    [Fact]
    public async Task Dispose_DetachesCurrentGroupPropertyChangedHandlers()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];

        Assert.Equal(1, TestHelpers.GetPropertyChangedSubscriberCount(group));

        vm.Dispose();

        Assert.Equal(0, TestHelpers.GetPropertyChangedSubscriberCount(group));
    }

    [Fact]
    public async Task Dispose_CleansUpTabsConcurrentlyDuringShutdown()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        var lineIndexDisposeTaskField = typeof(LogTabViewModel).GetField("_lineIndexDisposeTask", BindingFlags.Instance | BindingFlags.NonPublic);
        var isDisposedField = typeof(LogTabViewModel).GetField("_isDisposed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lineIndexDisposeTaskField);
        Assert.NotNull(isDisposedField);

        var monitors = new List<Task>();
        foreach (var tab in vm.Tabs)
        {
            var disposeCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lineIndexDisposeTaskField!.SetValue(tab, disposeCompleted.Task);

            monitors.Add(Task.Run(async () =>
            {
                while ((int)isDisposedField!.GetValue(tab)! == 0)
                    await Task.Delay(10);

                await Task.Delay(300);
                disposeCompleted.TrySetResult(true);
            }));
        }

        var stopwatch = Stopwatch.StartNew();
        vm.Dispose();
        stopwatch.Stop();

        await Task.WhenAll(monitors);

        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 700);
    }

    [Fact]
    public async Task ApplyImportedViewAsync_ReplacesExistingGroupsAndReusesKnownFiles()
    {
        var fileRepo = new StubLogFileRepository();
        var existingEntry = new LogFileEntry { FilePath = @"C:\logs\existing.log" };
        await fileRepo.AddAsync(existingEntry);

        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Old Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { existingEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();
        vm.ToggleGroupSelection(vm.Groups[0]);

        await vm.ApplyImportedViewAsync(new ViewExport
        {
            Groups = new List<ViewExportGroup>
            {
                new()
                {
                    Id = "folder-1",
                    Name = "Imported Folder",
                    Kind = LogGroupKind.Branch,
                    SortOrder = 0
                },
                new()
                {
                    Id = "dashboard-1",
                    Name = "Imported Dashboard",
                    ParentGroupId = "folder-1",
                    Kind = LogGroupKind.Dashboard,
                    SortOrder = 0,
                    FilePaths = new List<string> { @"C:\logs\existing.log", @"C:\logs\new.log" }
                }
            }
        });

        var persistedGroups = await groupRepo.GetAllAsync();
        Assert.Equal(2, persistedGroups.Count);
        Assert.DoesNotContain(persistedGroups, group => group.Name == "Old Dashboard");

        var importedFolder = persistedGroups.Single(group => group.Name == "Imported Folder");
        var importedDashboard = persistedGroups.Single(group => group.Name == "Imported Dashboard");
        Assert.Equal(importedFolder.Id, importedDashboard.ParentGroupId);
        Assert.Equal(LogGroupKind.Dashboard, importedDashboard.Kind);

        var storedFiles = await fileRepo.GetAllAsync();
        Assert.Equal(2, storedFiles.Count);
        Assert.Contains(storedFiles, file => file.FilePath == @"C:\logs\new.log");
        Assert.Contains(existingEntry.Id, importedDashboard.FileIds);
        Assert.Contains(storedFiles.Single(file => file.FilePath == @"C:\logs\new.log").Id, importedDashboard.FileIds);

        Assert.Null(vm.ActiveDashboardId);
        Assert.Equal(new[] { "Imported Folder", "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
    }

    [Fact]
    public void PaneState_DefaultsToBothOpen()
    {
        var vm = CreateViewModel();

        Assert.True(vm.IsGroupsPanelOpen);
        Assert.True(vm.IsSearchPanelOpen);
    }

    [Fact]
    public void ToggleFocusMode_TogglesBothPanes()
    {
        var vm = CreateViewModel();

        vm.ToggleFocusModeCommand.Execute(null);
        Assert.False(vm.IsGroupsPanelOpen);
        Assert.False(vm.IsSearchPanelOpen);

        vm.ToggleFocusModeCommand.Execute(null);
        Assert.True(vm.IsGroupsPanelOpen);
        Assert.True(vm.IsSearchPanelOpen);
    }

    [Fact]
    public void RememberPanelWidths_IgnoresSmallValues()
    {
        var vm = CreateViewModel();

        vm.RememberGroupsPanelWidth(280);
        vm.RememberSearchPanelWidth(410);
        vm.RememberGroupsPanelWidth(30);
        vm.RememberSearchPanelWidth(20);

        Assert.Equal(280, vm.GroupsPanelWidth);
        Assert.Equal(410, vm.SearchPanelWidth);
    }
}
