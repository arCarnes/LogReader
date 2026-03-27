using LogReader.App.ViewModels;
using LogReader.App.Services;
using LogReader.App.Views;
using LogReader.App.Models;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LogReader.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "LogReaderMainViewModelTests_" + Guid.NewGuid().ToString("N")[..8]);

    public MainViewModelTests()
    {
        AppPaths.SetRootPathForTests(_testRoot);
    }

    public void Dispose()
    {
        AppPaths.SetRootPathForTests(null);

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    // ─── Stubs (test-specific — shared stubs are in LogReader.Testing/Stubs.cs) ────────────────

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

        public async Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        {
            await Task.Yield();
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            return _entries
                .Where(entry => idSet.Contains(entry.Id))
                .ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        }

        public async Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
        {
            await Task.Yield();
            var pathSet = filePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _entries
                .Where(entry => pathSet.Contains(entry.FilePath))
                .ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<IReadOnlyDictionary<string, LogFileEntry>> GetOrCreateByPathsAsync(IEnumerable<string> filePaths)
        {
            await Task.Yield();
            var result = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                result[filePath] = GetOrCreateEntry(filePath);

            return result;
        }

        public async Task<LogFileEntry> GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc = null)
        {
            await Task.Yield();
            var entry = GetOrCreateEntry(filePath);
            if (lastOpenedAtUtc.HasValue)
                entry.LastOpenedAt = lastOpenedAtUtc.Value;

            return entry;
        }

        private LogFileEntry GetOrCreateEntry(string filePath)
        {
            var existing = _entries.FirstOrDefault(entry =>
                string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            var entry = new LogFileEntry { FilePath = filePath };
            _entries.Add(entry);
            return entry;
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

    private sealed class CountingLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries;

        public CountingLogFileRepository(IEnumerable<LogFileEntry>? entries = null)
        {
            _entries = entries?.ToList() ?? new List<LogFileEntry>();
        }

        public int GetAllCallCount { get; private set; }

        public void ResetGetAllCallCount() => GetAllCallCount = 0;

        public Task<List<LogFileEntry>> GetAllAsync()
        {
            GetAllCallCount++;
            return Task.FromResult(_entries.ToList());
        }

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
                _entries
                    .Where(entry => idSet.Contains(entry.Id))
                    .ToDictionary(entry => entry.Id, StringComparer.Ordinal));
        }

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
        {
            var pathSet = filePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
                _entries
                    .Where(entry => pathSet.Contains(entry.FilePath))
                    .ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase));
        }

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetOrCreateByPathsAsync(IEnumerable<string> filePaths)
        {
            var result = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                result[filePath] = GetOrCreateEntry(filePath);

            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(result);
        }

        public Task<LogFileEntry> GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc = null)
        {
            var entry = GetOrCreateEntry(filePath);
            if (lastOpenedAtUtc.HasValue)
                entry.LastOpenedAt = lastOpenedAtUtc.Value;

            return Task.FromResult(entry);
        }

        private LogFileEntry GetOrCreateEntry(string filePath)
        {
            var existing = _entries.FirstOrDefault(entry =>
                string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            var entry = new LogFileEntry { FilePath = filePath };
            _entries.Add(entry);
            return entry;
        }

        public Task AddAsync(LogFileEntry entry)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LogFileEntry entry)
        {
            var index = _entries.FindIndex(existing => existing.Id == entry.Id);
            if (index >= 0)
                _entries[index] = entry;

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id)
        {
            _entries.RemoveAll(entry => entry.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingImportExportLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public ViewExport? ImportResult { get; set; }
        public string? LastImportPath { get; private set; }
        public string? LastExportPath { get; private set; }
        public int ExportCallCount { get; private set; }
        public List<string> CallSequence { get; } = new();

        public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());

        public Task<LogGroup?> GetByIdAsync(string id)
            => Task.FromResult(_groups.FirstOrDefault(group => group.Id == id));

        public Task AddAsync(LogGroup group)
        {
            _groups.Add(group);
            CallSequence.Add($"Add:{group.Name}");
            return Task.CompletedTask;
        }

        public Task ReplaceAllAsync(IReadOnlyList<LogGroup> groups)
        {
            _groups.Clear();
            _groups.AddRange(groups);
            CallSequence.Add("ReplaceAll");
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LogGroup group)
        {
            var index = _groups.FindIndex(existing => existing.Id == group.Id);
            if (index >= 0)
                _groups[index] = group;

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id)
        {
            var existing = _groups.FirstOrDefault(group => group.Id == id);
            if (existing != null)
            {
                _groups.Remove(existing);
                CallSequence.Add($"Delete:{existing.Name}");
            }

            return Task.CompletedTask;
        }

        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;

        public Task ExportViewAsync(string exportPath)
        {
            ExportCallCount++;
            LastExportPath = exportPath;
            CallSequence.Add($"Export:{exportPath}");
            return Task.CompletedTask;
        }

        public Task<ViewExport?> ImportViewAsync(string importPath)
        {
            LastImportPath = importPath;
            CallSequence.Add($"Import:{importPath}");
            return Task.FromResult(ImportResult);
        }
    }

    private sealed class StubPersistedStateRecoveryCoordinator : IPersistedStateRecoveryCoordinator
    {
        public Func<PersistedStateRecoveryException, PersistedStateRecoveryResult> OnRecover { get; set; }
            = exception => new PersistedStateRecoveryResult(
                exception.StoreDisplayName,
                exception.StorePath,
                exception.StorePath + ".backup",
                exception.StorePath + ".backup.note.txt",
                exception.FailureReason);

        public int CallCount { get; private set; }

        public PersistedStateRecoveryException? LastException { get; private set; }

        public PersistedStateRecoveryResult Recover(PersistedStateRecoveryException exception)
        {
            CallCount++;
            LastException = exception;
            return OnRecover(exception);
        }
    }

    private sealed class ThrowOnGetAfterReplaceLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public int ReplaceAllCallCount { get; private set; }

        public Task<List<LogGroup>> GetAllAsync()
        {
            if (ReplaceAllCallCount > 0)
                throw new InvalidOperationException("GetAllAsync should not be called after ReplaceAllAsync.");

            return Task.FromResult(_groups.Select(CloneGroup).ToList());
        }

        public Task<LogGroup?> GetByIdAsync(string id)
            => Task.FromResult(_groups.FirstOrDefault(group => group.Id == id));

        public Task AddAsync(LogGroup group)
        {
            _groups.Add(CloneGroup(group));
            return Task.CompletedTask;
        }

        public Task ReplaceAllAsync(IReadOnlyList<LogGroup> groups)
        {
            ReplaceAllCallCount++;
            _groups.Clear();
            _groups.AddRange(groups.Select(CloneGroup));
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LogGroup group)
        {
            var index = _groups.FindIndex(existing => existing.Id == group.Id);
            if (index >= 0)
                _groups[index] = CloneGroup(group);

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id)
        {
            _groups.RemoveAll(group => group.Id == id);
            return Task.CompletedTask;
        }

        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;

        public Task ExportViewAsync(string exportPath) => Task.CompletedTask;

        public Task<ViewExport?> ImportViewAsync(string importPath)
            => Task.FromResult<ViewExport?>(null);
    }

    private sealed class BlockingViewportRefreshLogReader : ILogReaderService
    {
        private readonly TaskCompletionSource<bool> _blockedReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseBlockedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readLinesCallCount;

        public Task BlockedReadStarted => _blockedReadStarted.Task;

        public void ReleaseBlockedRead() => _releaseBlockedRead.TrySetResult(true);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = 100
            };
            index.LineOffsets.Add(0L);
            return Task.FromResult(index);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public async Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _readLinesCallCount) == 2)
            {
                _blockedReadStarted.TrySetResult(true);
                await _releaseBlockedRead.Task.WaitAsync(ct);
            }

            return new List<string> { "line 1" };
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult("line 1");
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
        ISettingsRepository? settingsRepo = null,
        IFileTailService? tailService = null,
        ILogReaderService? logReader = null,
        ISearchService? searchService = null,
        IEncodingDetectionService? encodingDetectionService = null,
        ILogTimestampNavigationService? timestampNavigationService = null,
        IFileDialogService? fileDialogService = null,
        IMessageBoxService? messageBoxService = null,
        ISettingsDialogService? settingsDialogService = null,
        IBulkOpenPathsDialogService? bulkOpenPathsDialogService = null,
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory = null,
        IPersistedStateRecoveryCoordinator? persistedStateRecoveryCoordinator = null)
    {
        return new MainViewModel(
            fileRepo ?? new StubLogFileRepository(),
            groupRepo ?? new StubLogGroupRepository(),
            settingsRepo ?? new StubSettingsRepository(),
            logReader ?? new StubLogReaderService(),
            searchService ?? new StubSearchService(),
            tailService ?? new StubFileTailService(),
            encodingDetectionService ?? new FileEncodingDetectionService(),
            timestampNavigationService ?? new LogTimestampNavigationService(),
            enableLifecycleTimer: false,
            fileDialogService: fileDialogService,
            messageBoxService: messageBoxService,
            settingsDialogService: settingsDialogService,
            bulkOpenPathsDialogService: bulkOpenPathsDialogService,
            settingsViewModelFactory: settingsViewModelFactory,
            persistedStateRecoveryCoordinator: persistedStateRecoveryCoordinator);
    }

    private static IReadOnlyDictionary<string, long> GetOpenOrderMap(MainViewModel vm) => vm.TabOpenOrder;

    private static IReadOnlyDictionary<string, long> GetPinOrderMap(MainViewModel vm) => vm.TabPinOrder;

    private static ViewExport CreateImportedView(string dashboardName = "Imported Dashboard", params string[] filePaths)
    {
        return new ViewExport
        {
            Groups = new List<ViewExportGroup>
            {
                new()
                {
                    Name = dashboardName,
                    Kind = LogGroupKind.Dashboard,
                    SortOrder = 0,
                    FilePaths = filePaths.ToList()
                }
            }
        };
    }

    private static void WriteInvalidStoreFile(string fileName, string content = "{ invalid json")
    {
        var storePath = JsonStore.GetFilePath(fileName);
        var directory = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(storePath, content);
    }

    private static LogGroup CloneGroup(LogGroup group)
    {
        return new LogGroup
        {
            Id = group.Id,
            Name = group.Name,
            ParentGroupId = group.ParentGroupId,
            Kind = group.Kind,
            SortOrder = group.SortOrder,
            FileIds = group.FileIds.ToList()
        };
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

    private async Task<(MainViewModel Vm, LogGroupViewModel Dashboard, GroupFileMemberViewModel Member)> ApplyDashboardModifierForSingleFileAsync(
        string basePath,
        string findPattern,
        string replacePattern,
        int daysBack = 1)
    {
        var fileEntry = new LogFileEntry { FilePath = basePath };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileEntry);
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "dashboard-1",
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        var dashboard = vm.Groups.Single();
        await vm.ApplyDashboardModifierAsync(
            dashboard,
            daysBack,
            new ReplacementPattern
            {
                Id = "pattern-1",
                FindPattern = findPattern,
                ReplacePattern = replacePattern
            });

        await WaitForConditionAsync(() => dashboard.MemberFiles.Count == 1);
        return (vm, dashboard, dashboard.MemberFiles[0]);
    }

    [Fact]
    public async Task InitializeAsync_DoesNotSeedRootBranch_WhenNoGroups()
    {
        var vm = new MainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
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
    public async Task OpenFileCommand_UsesInjectedFileDialogService()
    {
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = request =>
            {
                Assert.Equal("Open Log File", request.Title);
                Assert.True(request.Multiselect);
                return new OpenFileDialogResult(true, new[] { @"C:\test\one.log", @"C:\test\two.log" });
            }
        };
        var vm = CreateViewModel(fileDialogService: fileDialogService);
        await vm.InitializeAsync();

        await vm.OpenFileCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal(@"C:\test\one.log", vm.Tabs[0].FilePath);
        Assert.Equal(@"C:\test\two.log", vm.Tabs[1].FilePath);
    }

    [Fact]
    public async Task AddFilesToDashboardAsync_UsesInjectedFileDialogService()
    {
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = request =>
            {
                Assert.Equal("Add Files to Dashboard", request.Title);
                Assert.True(request.Multiselect);
                return new OpenFileDialogResult(true, new[] { @"C:\logs\app.log", @"C:\logs\api.log" });
            }
        };
        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService);
        await vm.InitializeAsync();

        await vm.AddFilesToDashboardAsync(vm.Groups[0]);

        Assert.Equal(2, vm.Groups[0].MemberFiles.Count);
        Assert.Contains(vm.Groups[0].MemberFiles, file => file.FilePath == @"C:\logs\app.log");
        Assert.Contains(vm.Groups[0].MemberFiles, file => file.FilePath == @"C:\logs\api.log");
    }

    [Fact]
    public async Task BulkAddFilesToDashboardAsync_UsesInjectedBulkOpenPathsDialogService()
    {
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var bulkOpenPathsDialogService = new StubBulkOpenPathsDialogService
        {
            OnShowDialog = request =>
            {
                Assert.Equal(BulkOpenPathsScope.Dashboard, request.Scope);
                Assert.Equal("Bulk Open Files", request.Title);
                Assert.Equal("Dashboard", request.TargetName);
                return new BulkOpenPathsDialogResult(
                    true,
                    string.Join(
                        Environment.NewLine,
                        "  \"C:\\logs\\app.log\"  ",
                        string.Empty,
                        "'C:\\logs\\api.log'",
                        "\"C:\\logs\\app.log\""));
            }
        };
        var vm = CreateViewModel(groupRepo: groupRepo, bulkOpenPathsDialogService: bulkOpenPathsDialogService);
        await vm.InitializeAsync();

        await vm.BulkAddFilesToDashboardAsync(vm.Groups[0]);

        Assert.Equal(2, vm.Groups[0].MemberFiles.Count);
        Assert.Contains(vm.Groups[0].MemberFiles, file => file.FilePath == @"C:\logs\app.log");
        Assert.Contains(vm.Groups[0].MemberFiles, file => file.FilePath == @"C:\logs\api.log");
    }

    [Fact]
    public async Task BulkAddFilesToDashboardAsync_BlankSubmissionMakesNoChanges()
    {
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var bulkOpenPathsDialogService = new StubBulkOpenPathsDialogService
        {
            OnShowDialog = static _ => new BulkOpenPathsDialogResult(true, "   \r\n\t")
        };
        var vm = CreateViewModel(groupRepo: groupRepo, bulkOpenPathsDialogService: bulkOpenPathsDialogService);
        await vm.InitializeAsync();

        await vm.BulkAddFilesToDashboardAsync(vm.Groups[0]);

        Assert.Empty(vm.Groups[0].MemberFiles);
    }

    [Fact]
    public async Task BulkOpenAdHocFilesCommand_UsesAdHocScope()
    {
        var bulkOpenPathsDialogService = new StubBulkOpenPathsDialogService
        {
            OnShowDialog = request =>
            {
                Assert.Equal(BulkOpenPathsScope.AdHoc, request.Scope);
                Assert.Null(request.TargetName);
                return new BulkOpenPathsDialogResult(
                    true,
                    string.Join(
                        Environment.NewLine,
                        @"C:\logs\bulk-a.log",
                        @"C:\logs\bulk-b.log"));
            }
        };
        var vm = CreateViewModel(bulkOpenPathsDialogService: bulkOpenPathsDialogService);
        await vm.InitializeAsync();

        await vm.BulkOpenAdHocFilesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal(@"C:\logs\bulk-a.log", vm.Tabs[0].FilePath);
        Assert.Equal(@"C:\logs\bulk-b.log", vm.Tabs[1].FilePath);
        Assert.True(vm.IsAdHocScopeActive);
    }

    [Fact]
    public async Task BulkAddFilesToActiveDashboardCommand_UsesActiveDashboard()
    {
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var bulkOpenPathsDialogService = new StubBulkOpenPathsDialogService
        {
            OnShowDialog = request =>
            {
                Assert.Equal(BulkOpenPathsScope.Dashboard, request.Scope);
                Assert.Equal("Dashboard", request.TargetName);
                return new BulkOpenPathsDialogResult(true, @"C:\logs\bulk.log");
            }
        };
        var vm = CreateViewModel(groupRepo: groupRepo, bulkOpenPathsDialogService: bulkOpenPathsDialogService);
        await vm.InitializeAsync();

        Assert.False(vm.CanAddFilesToActiveDashboard);

        vm.ToggleGroupSelection(vm.Groups[0]);

        Assert.True(vm.CanAddFilesToActiveDashboard);

        await vm.BulkAddFilesToActiveDashboardCommand.ExecuteAsync(null);

        Assert.Single(vm.Groups[0].MemberFiles);
        Assert.Equal(@"C:\logs\bulk.log", vm.Groups[0].MemberFiles[0].FilePath);
    }

    [Fact]
    public async Task RemoveFileFromDashboardAsync_RemovesMembershipAndUpdatesMemberFiles()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\app.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\api.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);

        var groupRepo = new RecordingImportExportLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "dashboard-1",
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileA.Id, fileB.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        Assert.Equal(new[] { fileA.Id, fileB.Id }, vm.Groups[0].MemberFiles.Select(member => member.FileId).ToArray());

        await vm.RemoveFileFromDashboardAsync(vm.Groups[0], fileA.Id);

        Assert.Equal(new[] { fileB.Id }, vm.Groups[0].Model.FileIds);
        Assert.Equal(new[] { fileB.Id }, vm.Groups[0].MemberFiles.Select(member => member.FileId).ToArray());

        var persisted = await groupRepo.GetByIdAsync("dashboard-1");
        Assert.NotNull(persisted);
        Assert.Equal(new[] { fileB.Id }, persisted!.FileIds);
    }

    [Fact]
    public async Task DashboardTreeView_RemoveMenuContext_ResolvesDashboardFromPlacementTarget()
    {
        await SingleThreadSynchronizationContext.RunAsync(() =>
        {
            var groupVm = new LogGroupViewModel(
                new LogGroup
                {
                    Id = "dashboard-1",
                    Name = "Dashboard",
                    Kind = LogGroupKind.Dashboard
                },
                _ => Task.CompletedTask);
            var fileVm = new GroupFileMemberViewModel("file-1", "app.log", @"C:\logs\app.log", showFullPath: false);
            var placementTarget = new Border
            {
                DataContext = fileVm,
                Tag = groupVm
            };
            var contextMenu = new ContextMenu
            {
                PlacementTarget = placementTarget
            };
            var menuItem = new MenuItem { Header = "Remove from Dashboard" };
            contextMenu.Items.Add(menuItem);

            var resolved = DashboardTreeView.TryGetDashboardFileMenuContext(menuItem, out var resolvedFileVm, out var resolvedGroupVm);

            Assert.True(resolved);
            Assert.Same(fileVm, resolvedFileVm);
            Assert.Same(groupVm, resolvedGroupVm);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DashboardTreeView_ShouldIgnoreGroupRowMouseDown_ForButtonDescendant()
    {
        await SingleThreadSynchronizationContext.RunAsync(() =>
        {
            var row = new Grid();
            var button = new Button();
            row.Children.Add(button);

            Assert.True(DashboardTreeView.ShouldIgnoreGroupRowMouseDown(button, row));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DashboardTreeView_ShouldIgnoreGroupRowMouseDown_ForTextBoxDescendant()
    {
        await SingleThreadSynchronizationContext.RunAsync(() =>
        {
            var row = new Grid();
            var textBox = new TextBox();
            row.Children.Add(textBox);

            Assert.True(DashboardTreeView.ShouldIgnoreGroupRowMouseDown(textBox, row));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DashboardTreeView_ShouldIgnoreGroupRowMouseDown_ReturnsFalseForPlainText()
    {
        await SingleThreadSynchronizationContext.RunAsync(() =>
        {
            var row = new Grid();
            var textBlock = new TextBlock();
            row.Children.Add(textBlock);

            Assert.False(DashboardTreeView.ShouldIgnoreGroupRowMouseDown(textBlock, row));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DashboardTreeView_GroupExpandMouseDown_TogglesExpandWithoutChangingScope()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var vm = CreateViewModel();
            await vm.InitializeAsync();
            await vm.CreateContainerGroupCommand.ExecuteAsync(null);
            var branch = vm.Groups[0];
            await vm.CreateChildGroupAsync(branch, LogGroupKind.Branch);

            branch = vm.Groups.First(group => group.Id == branch.Id);
            branch.IsExpanded = false;

            var view = (DashboardTreeView)RuntimeHelpers.GetUninitializedObject(typeof(DashboardTreeView));
            var sender = new TextBlock
            {
                DataContext = branch
            };
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = sender
            };

            typeof(DashboardTreeView)
                .GetMethod("GroupExpand_MouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, new object[] { sender, args });

            Assert.True(branch.IsExpanded);
            Assert.Null(vm.ActiveDashboardId);
        });
    }

    [Fact]
    public void FormatModifierActionLabel_ReturnsPlainDayOffset()
    {
        var label = MainViewModel.FormatModifierActionLabel(
            1,
            new ReplacementPattern
            {
                FindPattern = ".log",
                ReplacePattern = ".log{yyyyMMdd}"
            });

        Assert.Equal("T-1", label);
    }

    [Fact]
    public void FormatModifierPatternLabel_UsesResolvedTargetDate()
    {
        var label = MainViewModel.FormatModifierPatternLabel(
            2,
            new ReplacementPattern
            {
                Name = "Date",
                FindPattern = ".log",
                ReplacePattern = ".log{yyyyMMdd}"
            });

        Assert.Contains($".log{DateTime.Today.AddDays(-2):yyyyMMdd}", label);
        Assert.DoesNotContain("yyyyMMdd", label);
    }

    [Fact]
    public async Task ApplyDashboardModifierAsync_UsesEffectivePathsForDisplayAndScope()
    {
        Directory.CreateDirectory(_testRoot);
        var targetDate = DateTime.Today.AddDays(-1);
        var basePath = Path.Combine(_testRoot, "dashboard.log");
        var effectivePath = $"{basePath}{targetDate:yyyyMMdd}";
        await File.WriteAllTextAsync(basePath, "base");
        await File.WriteAllTextAsync(effectivePath, "effective");

        var fileEntry = new LogFileEntry { FilePath = basePath };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileEntry);
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "dashboard-1",
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        var dashboard = vm.Groups.Single();
        await vm.ApplyDashboardModifierAsync(
            dashboard,
            daysBack: 1,
            new ReplacementPattern
            {
                Id = "pattern-1",
                FindPattern = ".log",
                ReplacePattern = ".log{yyyyMMdd}"
            });

        Assert.Equal("Dashboard [T-1]", dashboard.DisplayName);
        Assert.Contains(dashboard.MemberFiles, member => string.Equals(member.FilePath, effectivePath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.FilteredTabs, tab => string.Equals(tab.FilePath, effectivePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApplyDashboardModifierAsync_WithOrderedPatterns_FallsBackToNextExistingMatch()
    {
        Directory.CreateDirectory(_testRoot);
        var targetDate = DateTime.Today.AddDays(-1);
        var basePath = Path.Combine(_testRoot, "dashboard.log");
        var firstCandidate = $"{basePath}.{targetDate:yyyy-MM-dd}";
        var secondCandidate = $"{basePath}{targetDate:yyyyMMdd}";
        await File.WriteAllTextAsync(basePath, "base");
        await File.WriteAllTextAsync(secondCandidate, "effective");

        var fileEntry = new LogFileEntry { FilePath = basePath };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileEntry);
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "dashboard-1",
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        var dashboard = vm.Groups.Single();
        await vm.ApplyDashboardModifierAsync(
            dashboard,
            daysBack: 1,
            new[]
            {
                new ReplacementPattern
                {
                    Id = "pattern-1",
                    Name = "Missing first",
                    FindPattern = ".log",
                    ReplacePattern = ".log.{yyyy-MM-dd}"
                },
                new ReplacementPattern
                {
                    Id = "pattern-2",
                    Name = "Existing second",
                    FindPattern = ".log",
                    ReplacePattern = ".log{yyyyMMdd}"
                }
            });

        await WaitForConditionAsync(() =>
            dashboard.MemberFiles.Count == 1 &&
            string.Equals(dashboard.MemberFiles[0].FilePath, secondCandidate, StringComparison.OrdinalIgnoreCase) &&
            vm.FilteredTabs.Count() == 1 &&
            string.Equals(vm.FilteredTabs.Single().FilePath, secondCandidate, StringComparison.OrdinalIgnoreCase));

        var memberPaths = dashboard.MemberFiles.Select(member => member.FilePath).ToArray();
        var filteredTabPaths = vm.FilteredTabs.Select(tab => tab.FilePath).ToArray();
        Assert.DoesNotContain(memberPaths, path => string.Equals(path, firstCandidate, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(memberPaths, path => string.Equals(path, secondCandidate, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(filteredTabPaths, path => string.Equals(path, secondCandidate, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApplyDashboardModifierAsync_ResolvesCommonDateShiftFormatsFromUndatedBasePath()
    {
        var targetDate = DateTime.Today.AddDays(-1);
        var cases = new (string Name, string FindPattern, string ReplacePattern, Func<string, DateTime, string> ExpectedPathFactory)[]
        {
            ("app.log.YYYY-MM-DD", ".log", ".log.{yyyy-MM-dd}", (root, date) => Path.Combine(root, "logs", $"app.log.{date:yyyy-MM-dd}")),
            ("app-YYYYMMDD.log", ".log", "-{yyyyMMdd}.log", (root, date) => Path.Combine(root, "logs", $"app-{date:yyyyMMdd}.log")),
            ("app.YYYY-MM-DD.log", ".log", ".{yyyy-MM-dd}.log", (root, date) => Path.Combine(root, "logs", $"app.{date:yyyy-MM-dd}.log")),
            ("app.log.YYYY-MM", ".log", ".log.{yyyy-MM}", (root, date) => Path.Combine(root, "logs", $"app.log.{date:yyyy-MM}")),
            ("app-YYYYMM.log", ".log", "-{yyyyMM}.log", (root, date) => Path.Combine(root, "logs", $"app-{date:yyyyMM}.log")),
            ("app.log.YYYY-MM-DD-15", ".log", ".log.{yyyy-MM-dd}-15", (root, date) => Path.Combine(root, "logs", $"app.log.{date:yyyy-MM-dd}-15")),
            ("app-YYYYMMDD-15.log", ".log", "-{yyyyMMdd}-15.log", (root, date) => Path.Combine(root, "logs", $"app-{date:yyyyMMdd}-15.log")),
            ("app.log.YYYY-MM-DD_15-30", ".log", ".log.{yyyy-MM-dd}_15-30", (root, date) => Path.Combine(root, "logs", $"app.log.{date:yyyy-MM-dd}_15-30")),
            ("app-YYYYMMDDT153000.log", ".log", "-{yyyyMMdd}T153000.log", (root, date) => Path.Combine(root, "logs", $"app-{date:yyyyMMdd}T153000.log")),
            ("app.log.YYYY-MM-DD.1", ".log", ".log.{yyyy-MM-dd}.1", (root, date) => Path.Combine(root, "logs", $"app.log.{date:yyyy-MM-dd}.1")),
            ("app-YYYYMMDD-001.log", ".log", "-{yyyyMMdd}-001.log", (root, date) => Path.Combine(root, "logs", $"app-{date:yyyyMMdd}-001.log")),
            ("logs/YYYY/MM/DD/app.log", "app.log", "{yyyy}\\{MM}\\{dd}\\app.log", (root, date) => Path.Combine(root, "logs", $"{date:yyyy}", $"{date:MM}", $"{date:dd}", "app.log")),
            ("logs/YYYY-MM-DD/app.log", "app.log", "{yyyy-MM-dd}\\app.log", (root, date) => Path.Combine(root, "logs", $"{date:yyyy-MM-dd}", "app.log"))
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var testCase = cases[index];
            var caseRoot = Path.Combine(_testRoot, $"date-shift-format-{index:00}");
            var basePath = Path.Combine(caseRoot, "logs", "app.log");
            var expectedPath = testCase.ExpectedPathFactory(caseRoot, targetDate);

            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            await File.WriteAllTextAsync(basePath, "base");
            await File.WriteAllTextAsync(expectedPath, "effective");

            var (vm, dashboard, member) = await ApplyDashboardModifierForSingleFileAsync(
                basePath,
                testCase.FindPattern,
                testCase.ReplacePattern);

            await WaitForConditionAsync(() =>
                vm.FilteredTabs.Count() == 1 &&
                string.Equals(vm.FilteredTabs.Single().FilePath, expectedPath, StringComparison.OrdinalIgnoreCase));

            Assert.Equal("Dashboard [T-1]", dashboard.DisplayName);
            Assert.False(member.HasError, testCase.Name);
            Assert.Equal(expectedPath, member.FilePath, ignoreCase: true);
            Assert.Equal(expectedPath, vm.FilteredTabs.Single().FilePath, ignoreCase: true);
        }
    }

    [Fact]
    public async Task ApplyDashboardModifierAsync_WithTimeTokens_TargetsMidnightAndMissesNonMidnightArchive()
    {
        var targetDate = DateTime.Today.AddDays(-1);
        var caseRoot = Path.Combine(_testRoot, "date-shift-time-tokens");
        var basePath = Path.Combine(caseRoot, "logs", "app.log");
        var existingArchivePath = Path.Combine(caseRoot, "logs", $"app-{targetDate:yyyyMMdd}T153000.log");
        var expectedPath = Path.Combine(caseRoot, "logs", $"app-{targetDate:yyyyMMdd}T000000.log");
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        await File.WriteAllTextAsync(basePath, "base");
        await File.WriteAllTextAsync(existingArchivePath, "effective");

        var (_, dashboard, member) = await ApplyDashboardModifierForSingleFileAsync(
            basePath,
            ".log",
            "-{yyyyMMdd}T{HHmmss}.log");

        Assert.Equal("Dashboard [T-1]", dashboard.DisplayName);
        Assert.True(member.HasError);
        Assert.Equal("File not found", member.ErrorMessage);
        Assert.Equal(expectedPath, member.FilePath, ignoreCase: true);
    }

    [Fact]
    public async Task ApplyDashboardModifierAsync_WithAlreadyDatedBasePath_DoesNotParseEmbeddedDate()
    {
        var today = DateTime.Today;
        var targetDate = today.AddDays(-1);
        var caseRoot = Path.Combine(_testRoot, "date-shift-dated-base");
        var basePath = Path.Combine(caseRoot, "logs", $"app-{today:yyyyMMdd}.log");
        var existingPriorDayPath = Path.Combine(caseRoot, "logs", $"app-{targetDate:yyyyMMdd}.log");
        var expectedPath = Path.Combine(caseRoot, "logs", $"app-{today:yyyyMMdd}-{targetDate:yyyyMMdd}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        await File.WriteAllTextAsync(basePath, "base");
        await File.WriteAllTextAsync(existingPriorDayPath, "effective");

        var (_, dashboard, member) = await ApplyDashboardModifierForSingleFileAsync(
            basePath,
            ".log",
            "-{yyyyMMdd}.log");

        Assert.Equal("Dashboard [T-1]", dashboard.DisplayName);
        Assert.True(member.HasError);
        Assert.Equal("File not found", member.ErrorMessage);
        Assert.Equal(expectedPath, member.FilePath, ignoreCase: true);
    }

    [Fact]
    public async Task ClearDashboardModifierAsync_RestoresBaseDisplayAndScope()
    {
        Directory.CreateDirectory(_testRoot);
        var targetDate = DateTime.Today.AddDays(-1);
        var basePath = Path.Combine(_testRoot, "restore.log");
        var effectivePath = $"{basePath}{targetDate:yyyyMMdd}";
        await File.WriteAllTextAsync(basePath, "base");
        await File.WriteAllTextAsync(effectivePath, "effective");

        var fileEntry = new LogFileEntry { FilePath = basePath };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileEntry);
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "dashboard-1",
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        var dashboard = vm.Groups.Single();
        await vm.ApplyDashboardModifierAsync(
            dashboard,
            daysBack: 1,
            new ReplacementPattern
            {
                Id = "pattern-1",
                FindPattern = ".log",
                ReplacePattern = ".log{yyyyMMdd}"
            });

        await vm.ClearDashboardModifierAsync(dashboard);

        Assert.Equal("Dashboard", dashboard.DisplayName);
        Assert.Contains(dashboard.MemberFiles, member => string.Equals(member.FilePath, basePath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.FilteredTabs, tab => string.Equals(tab.FilePath, basePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApplyAdHocModifierAsync_UsesCurrentAdHocFilesAsBaseSnapshot()
    {
        Directory.CreateDirectory(_testRoot);
        var targetDate = DateTime.Today.AddDays(-1);
        var basePath = Path.Combine(_testRoot, "adhoc.log");
        var effectivePath = $"{basePath}{targetDate:yyyyMMdd}";
        await File.WriteAllTextAsync(basePath, "base");
        await File.WriteAllTextAsync(effectivePath, "effective");

        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(basePath);

        await vm.ApplyAdHocModifierAsync(
            daysBack: 1,
            new ReplacementPattern
            {
                Id = "pattern-1",
                FindPattern = ".log",
                ReplacePattern = ".log{yyyyMMdd}"
            });

        Assert.True(vm.IsAdHocScopeActive);
        Assert.Equal("Ad Hoc [T-1]", vm.CurrentScopeLabel);
        Assert.Equal("Ad Hoc [T-1] (1)", vm.AdHocScopeChipText);
        Assert.Contains(vm.FilteredTabs, tab => string.Equals(tab.FilePath, effectivePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApplyAdHocModifierAsync_WithOrderedPatterns_FallsBackToNextMatchingPattern()
    {
        Directory.CreateDirectory(_testRoot);
        var targetDate = DateTime.Today.AddDays(-1);
        var basePath = Path.Combine(_testRoot, "adhoc.log");
        var effectivePath = $"{basePath}{targetDate:yyyyMMdd}";
        await File.WriteAllTextAsync(basePath, "base");
        await File.WriteAllTextAsync(effectivePath, "effective");

        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(basePath);

        await vm.ApplyAdHocModifierAsync(
            daysBack: 1,
            new[]
            {
                new ReplacementPattern
                {
                    Id = "pattern-1",
                    Name = "No match",
                    FindPattern = ".txt",
                    ReplacePattern = ".txt{yyyyMMdd}"
                },
                new ReplacementPattern
                {
                    Id = "pattern-2",
                    Name = "Log suffix",
                    FindPattern = ".log",
                    ReplacePattern = ".log{yyyyMMdd}"
                }
            });

        Assert.Contains(vm.FilteredTabs, tab => string.Equals(tab.FilePath, effectivePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LogViewportView_UpdateViewportContextMenu_ShowsFileOpenActionsWhenScopeIsEmpty()
    {
        await SingleThreadSynchronizationContext.RunAsync(() =>
        {
            var contextMenu = new ContextMenu();
            var copyItem = new MenuItem { Tag = LogViewportView.CopySelectedLinesMenuItemTag };
            var openItem = new MenuItem { Tag = LogViewportView.OpenLogFileMenuItemTag };
            var bulkOpenItem = new MenuItem { Tag = LogViewportView.BulkOpenFilesMenuItemTag };
            contextMenu.Items.Add(copyItem);
            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(bulkOpenItem);

            LogViewportView.UpdateViewportContextMenu(contextMenu, isCurrentScopeEmpty: true);

            Assert.Equal(Visibility.Collapsed, copyItem.Visibility);
            Assert.Equal(Visibility.Visible, openItem.Visibility);
            Assert.Equal(Visibility.Visible, bulkOpenItem.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LogViewportView_UpdateViewportContextMenu_ShowsCopyActionWhenScopeHasTabs()
    {
        await SingleThreadSynchronizationContext.RunAsync(() =>
        {
            var contextMenu = new ContextMenu();
            var copyItem = new MenuItem { Tag = LogViewportView.CopySelectedLinesMenuItemTag };
            var openItem = new MenuItem { Tag = LogViewportView.OpenLogFileMenuItemTag };
            var bulkOpenItem = new MenuItem { Tag = LogViewportView.BulkOpenFilesMenuItemTag };
            contextMenu.Items.Add(copyItem);
            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(bulkOpenItem);

            LogViewportView.UpdateViewportContextMenu(contextMenu, isCurrentScopeEmpty: false);

            Assert.Equal(Visibility.Visible, copyItem.Visibility);
            Assert.Equal(Visibility.Collapsed, openItem.Visibility);
            Assert.Equal(Visibility.Collapsed, bulkOpenItem.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void LogViewportView_TryGetVerticalNavigationRequest_MapsScrollAndJumpKeys()
    {
        Assert.True(LogViewportView.TryGetVerticalNavigationRequest(Key.Up, ModifierKeys.None, 40, out var upRequest));
        Assert.Equal(LogViewportView.VerticalNavigationKind.ScrollByDelta, upRequest.Kind);
        Assert.Equal(-1, upRequest.ScrollDelta);

        Assert.True(LogViewportView.TryGetVerticalNavigationRequest(Key.PageDown, ModifierKeys.None, 40, out var pageDownRequest));
        Assert.Equal(LogViewportView.VerticalNavigationKind.ScrollByDelta, pageDownRequest.Kind);
        Assert.Equal(40, pageDownRequest.ScrollDelta);

        Assert.True(LogViewportView.TryGetVerticalNavigationRequest(Key.Home, ModifierKeys.None, 40, out var homeRequest));
        Assert.Equal(LogViewportView.VerticalNavigationKind.JumpToTop, homeRequest.Kind);
        Assert.Equal(0, homeRequest.ScrollDelta);

        Assert.True(LogViewportView.TryGetVerticalNavigationRequest(Key.End, ModifierKeys.None, 40, out var endRequest));
        Assert.Equal(LogViewportView.VerticalNavigationKind.JumpToBottom, endRequest.Kind);
        Assert.Equal(0, endRequest.ScrollDelta);
    }

    [Fact]
    public void LogViewportView_TryGetVerticalNavigationRequest_IgnoresModifiedAndUnsupportedKeys()
    {
        Assert.False(LogViewportView.TryGetVerticalNavigationRequest(Key.Up, ModifierKeys.Shift, 40, out _));
        Assert.False(LogViewportView.TryGetVerticalNavigationRequest(Key.PageDown, ModifierKeys.Control, 40, out _));
        Assert.False(LogViewportView.TryGetVerticalNavigationRequest(Key.C, ModifierKeys.Control, 40, out _));
        Assert.False(LogViewportView.TryGetVerticalNavigationRequest(Key.Left, ModifierKeys.None, 40, out _));
    }

    [Fact]
    public void LogViewportView_StickyAutoScrollExitHelpers_ClassifyIntentCorrectly()
    {
        Assert.True(LogViewportView.ShouldDisableStickyAutoScrollForMouseWheel(120));
        Assert.False(LogViewportView.ShouldDisableStickyAutoScrollForMouseWheel(-120));

        Assert.True(LogViewportView.TryGetVerticalNavigationRequest(Key.Up, ModifierKeys.None, 40, out var upRequest));
        Assert.True(LogViewportView.ShouldDisableStickyAutoScrollForVerticalNavigation(upRequest));

        Assert.True(LogViewportView.TryGetVerticalNavigationRequest(Key.PageDown, ModifierKeys.None, 40, out var pageDownRequest));
        Assert.False(LogViewportView.ShouldDisableStickyAutoScrollForVerticalNavigation(pageDownRequest));

        Assert.True(LogViewportView.TryGetVerticalNavigationRequest(Key.Home, ModifierKeys.None, 40, out var homeRequest));
        Assert.True(LogViewportView.ShouldDisableStickyAutoScrollForVerticalNavigation(homeRequest));

        Assert.True(LogViewportView.TryGetVerticalNavigationRequest(Key.End, ModifierKeys.None, 40, out var endRequest));
        Assert.False(LogViewportView.ShouldDisableStickyAutoScrollForVerticalNavigation(endRequest));

        Assert.True(LogViewportView.ShouldDisableStickyAutoScrollForScrollBar(MouseButton.Left));
        Assert.False(LogViewportView.ShouldDisableStickyAutoScrollForScrollBar(MouseButton.Right));
    }

    [Fact]
    public async Task LogViewportView_HandleMouseWheel_WhenStickyAutoScrollEnabled_DisablesGlobalAndMovesViewport()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tab = vm.Tabs.First(tab => tab.FilePath == @"C:\test\a.log");
        var startingScrollPosition = tab.ScrollPosition;

        var handled = LogViewportView.HandleMouseWheel(vm, tab, 120);

        Assert.True(handled);
        Assert.False(vm.GlobalAutoScrollEnabled);
        Assert.All(vm.Tabs, openTab => Assert.False(openTab.AutoScrollEnabled));
        Assert.Equal(Math.Max(0, startingScrollPosition - 3), tab.ScrollPosition);
    }

    [Fact]
    public async Task LogViewportView_TryExitStickyAutoScrollForScrollBar_DisablesGlobalAutoScroll()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        Assert.True(vm.GlobalAutoScrollEnabled);

        var exited = LogViewportView.TryExitStickyAutoScrollForScrollBar(vm, MouseButton.Left);

        Assert.True(exited);
        Assert.False(vm.GlobalAutoScrollEnabled);
        Assert.All(vm.Tabs, tab => Assert.False(tab.AutoScrollEnabled));
    }

    [Fact]
    public async Task OpenSettingsAsync_WhenDialogAccepted_SavesUpdatedSettings()
    {
        var settingsRepo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                LogFontFamily = "Consolas"
            }
        };
        var settingsDialogService = new StubSettingsDialogService
        {
            OnShowDialog = (settingsVm, owner) =>
            {
                Assert.Null(owner);
                settingsVm.DefaultOpenDirectory = @"C:\logs";
                settingsVm.LogFontFamily = "Cascadia Mono";
                settingsVm.AddDateRollingPatternCommand.Execute(null);
                settingsVm.DateRollingPatterns[0].Name = "Log4Net";
                settingsVm.DateRollingPatterns[0].FindPattern = ".log";
                settingsVm.DateRollingPatterns[0].ReplacePattern = ".log{yyyyMMdd}";
                return true;
            }
        };
        var vm = CreateViewModel(settingsRepo: settingsRepo, settingsDialogService: settingsDialogService);
        await vm.InitializeAsync();

        await vm.OpenSettingsAsync(null);

        Assert.Equal(@"C:\logs", settingsRepo.Settings.DefaultOpenDirectory);
        Assert.Equal("Cascadia Mono", settingsRepo.Settings.LogFontFamily);
        var savedPattern = Assert.Single(settingsRepo.Settings.DateRollingPatterns);
        Assert.Equal("Log4Net", savedPattern.Name);
        Assert.Equal(".log{yyyyMMdd}", savedPattern.ReplacePattern);

        var loadedPatterns = await vm.LoadReplacementPatternsAsync();
        Assert.Single(loadedPatterns);
    }

    [Fact]
    public async Task OpenSettingsAsync_WhenDialogCanceled_DoesNotPersistDateRollingPatternChanges()
    {
        var settingsRepo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                DateRollingPatterns = new List<ReplacementPattern>
                {
                    new() { Name = "Existing", FindPattern = ".log", ReplacePattern = ".log{yyyyMMdd}" }
                }
            }
        };
        var settingsDialogService = new StubSettingsDialogService
        {
            OnShowDialog = (settingsVm, owner) =>
            {
                settingsVm.DateRollingPatterns.Clear();
                settingsVm.AddDateRollingPatternCommand.Execute(null);
                settingsVm.DateRollingPatterns[0].Name = "Canceled";
                settingsVm.DateRollingPatterns[0].FindPattern = ".txt";
                settingsVm.DateRollingPatterns[0].ReplacePattern = ".txt{yyyyMMdd}";
                return false;
            }
        };
        var vm = CreateViewModel(settingsRepo: settingsRepo, settingsDialogService: settingsDialogService);
        await vm.InitializeAsync();

        await vm.OpenSettingsAsync(null);

        var savedPattern = Assert.Single(settingsRepo.Settings.DateRollingPatterns);
        Assert.Equal("Existing", savedPattern.Name);
    }

    [Fact]
    public async Task RunViewActionAsync_WhenOperationThrowsUnexpectedException_ShowsFriendlyError()
    {
        var messageBoxService = new StubMessageBoxService();
        var vm = CreateViewModel(messageBoxService: messageBoxService);

        await vm.RunViewActionAsync(
            () => Task.FromException(new IOException("Disk offline")),
            "Dashboard Action Failed");

        Assert.Equal("Dashboard Action Failed", messageBoxService.LastCaption);
        Assert.Contains("requested action could not be completed", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Disk offline", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunViewActionAsync_WhenOperationThrowsRecoveryFailure_ShowsRecoveryFailure()
    {
        var messageBoxService = new StubMessageBoxService();
        var vm = CreateViewModel(messageBoxService: messageBoxService);
        var recoveryException = new PersistedStateRecoveryException(
            "log file metadata",
            @"C:\logs\logfiles.json",
            "The saved log file metadata is not valid JSON.");
        var priorRecovery = new PersistedStateRecoveryResult(
            recoveryException.StoreDisplayName,
            recoveryException.StorePath,
            recoveryException.StorePath + ".backup",
            recoveryException.StorePath + ".backup.note.txt",
            recoveryException.FailureReason);

        await vm.RunViewActionAsync(
            () => Task.FromException(new RuntimePersistedStateRecoveryFailedException(recoveryException, priorRecovery)));

        Assert.Equal("LogReader Recovery Failed", messageBoxService.LastCaption);
        Assert.Contains("could not recover", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logfiles.json", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunViewActionAsync_WhenInlineRenameSaveFails_KeepsEditorStateAndShowsFriendlyError()
    {
        var messageBoxService = new StubMessageBoxService();
        var vm = CreateViewModel(messageBoxService: messageBoxService);
        var groupVm = new LogGroupViewModel(
            new LogGroup
            {
                Id = "dashboard-1",
                Name = "Current Dashboard",
                Kind = LogGroupKind.Dashboard
            },
            _ => Task.FromException(new IOException("Disk offline")));
        groupVm.BeginEdit();
        groupVm.EditName = "Renamed Dashboard";

        await vm.RunViewActionAsync(() => groupVm.CommitEditAsync());

        Assert.Equal("LogReader Error", messageBoxService.LastCaption);
        Assert.Contains("requested action could not be completed", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Disk offline", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(groupVm.IsEditing);
        Assert.Equal("Current Dashboard", groupVm.Name);
        Assert.Equal("Current Dashboard", groupVm.Model.Name);
        Assert.Equal("Renamed Dashboard", groupVm.EditName);
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

        var openOrder = GetOpenOrderMap(vm);
        var pinOrder = GetPinOrderMap(vm);
        Assert.Contains(tab.FileId, openOrder.Keys);
        Assert.Contains(tab.FileId, pinOrder.Keys);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.DoesNotContain(tab.FileId, openOrder.Keys);
        Assert.DoesNotContain(tab.FileId, pinOrder.Keys);
    }

    [Fact]
    public async Task CloseTab_DuringInFlightViewportRefresh_DoesNotBlock()
    {
        var reader = new BlockingViewportRefreshLogReader();
        var vm = CreateViewModel(logReader: reader);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\file.log");

        var tab = vm.Tabs[0];
        var refreshTask = tab.RefreshViewportAsync();
        await reader.BlockedReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var closeTask = vm.CloseTabCommand.ExecuteAsync(tab);
        var completedQuickly = await Task.WhenAny(closeTask, Task.Delay(500)) == closeTask;

        reader.ReleaseBlockedRead();
        await refreshTask;
        await closeTask;

        Assert.True(completedQuickly, "CloseTab blocked while viewport refresh was in-flight.");
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public async Task OpenFilePathAsync_SingleTabOpen_DoesNotTriggerFullDashboardMemberRefresh()
    {
        var fileEntry = new LogFileEntry { FilePath = @"C:\test\dashboard-member.log" };
        var fileRepo = new CountingLogFileRepository(new[] { fileEntry });
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();
        fileRepo.ResetGetAllCallCount();

        await vm.OpenFilePathAsync(fileEntry.FilePath);
        await WaitForConditionAsync(() =>
            vm.Groups.Count == 1 &&
            vm.Groups[0].MemberFiles.Count == 1 &&
            !vm.Groups[0].MemberFiles[0].HasError);

        Assert.Equal(0, fileRepo.GetAllCallCount);
    }

    [Fact]
    public async Task CloseTab_SingleTabClose_DoesNotTriggerFullDashboardMemberRefresh()
    {
        var fileEntry = new LogFileEntry { FilePath = @"C:\test\dashboard-member.log" };
        var fileRepo = new CountingLogFileRepository(new[] { fileEntry });
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(fileEntry.FilePath);
        await WaitForConditionAsync(() =>
            vm.Groups.Count == 1 &&
            vm.Groups[0].MemberFiles.Count == 1 &&
            !vm.Groups[0].MemberFiles[0].HasError);

        fileRepo.ResetGetAllCallCount();

        await vm.CloseTabCommand.ExecuteAsync(vm.SelectedTab);
        await WaitForConditionAsync(() =>
            vm.Groups.Count == 1 &&
            vm.Groups[0].MemberFiles.Count == 1 &&
            vm.Groups[0].MemberFiles[0].HasError);

        Assert.Equal(0, fileRepo.GetAllCallCount);
    }

    [Fact]
    public async Task PartialDashboardMemberRefresh_PreservesOrderingSelectionAndMissingFileState()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\test\dashboard-a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\test\dashboard-b.log" };
        var fileC = new LogFileEntry { FilePath = @"C:\test\dashboard-c.log" };
        var fileRepo = new CountingLogFileRepository(new[] { fileA, fileB, fileC });
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileA.Id, fileB.Id, fileC.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();
        var dashboard = Assert.Single(vm.Groups);
        vm.ToggleGroupSelection(dashboard);

        Assert.Equal(new[] { fileA.Id, fileB.Id, fileC.Id }, dashboard.MemberFiles.Select(member => member.FileId).ToArray());
        Assert.All(dashboard.MemberFiles, member => Assert.True(member.HasError));

        await vm.OpenFilePathAsync(fileA.FilePath);
        await vm.OpenFilePathAsync(fileB.FilePath);
        await WaitForConditionAsync(() =>
            dashboard.MemberFiles.Count == 3 &&
            dashboard.MemberFiles.Select(member => member.FileId).SequenceEqual(new[] { fileA.Id, fileB.Id, fileC.Id }) &&
            !dashboard.MemberFiles[0].HasError &&
            !dashboard.MemberFiles[1].HasError &&
            dashboard.MemberFiles[1].IsSelected &&
            dashboard.MemberFiles[2].HasError);

        await vm.CloseTabCommand.ExecuteAsync(vm.Tabs.Single(tab => tab.FileId == fileB.Id));
        await WaitForConditionAsync(() =>
            dashboard.MemberFiles.Count == 3 &&
            dashboard.MemberFiles.Select(member => member.FileId).SequenceEqual(new[] { fileA.Id, fileB.Id, fileC.Id }) &&
            dashboard.MemberFiles[0].IsSelected &&
            !dashboard.MemberFiles[0].HasError &&
            dashboard.MemberFiles[1].HasError &&
            dashboard.MemberFiles[2].HasError);
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
    public async Task ShowAdHocTabs_ClearsActiveDashboardAndUpdatesScopeState()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];
        dashboard.Name = "Payments";
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);

        vm.ToggleGroupSelection(dashboard);
        Assert.False(vm.IsAdHocScopeActive);
        Assert.Equal("Scope: Payments (1)", vm.CurrentScopeSummaryText);

        vm.ShowAdHocTabsCommand.Execute(null);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Null(vm.ActiveDashboardId);
        Assert.All(vm.Groups, group => Assert.False(group.IsSelected));
        Assert.True(vm.IsAdHocScopeActive);
        Assert.Equal("Ad Hoc", vm.CurrentScopeLabel);
        Assert.Equal("Scope: Ad Hoc (1)", vm.CurrentScopeSummaryText);
        Assert.Equal("Ad Hoc (1)", vm.AdHocScopeChipText);
        Assert.Equal("1 of 2 tabs (Ad Hoc)", vm.TabCountText);
        Assert.Single(filtered);
        Assert.Equal(@"C:\test\b.log", filtered[0].FilePath);
    }

    [Fact]
    public async Task ShowAdHocTabs_WhenAllOpenTabsAreAssigned_ShowsZeroCountWithoutChangingGroups()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\assigned.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        var groupIds = vm.Groups.Select(group => group.Id).ToArray();

        vm.ShowAdHocTabsCommand.Execute(null);

        Assert.True(vm.IsAdHocScopeActive);
        Assert.Equal("Ad Hoc (0)", vm.AdHocScopeChipText);
        Assert.True(vm.IsCurrentScopeEmpty);
        Assert.Equal(groupIds, vm.Groups.Select(group => group.Id).ToArray());
    }

    [Fact]
    public async Task EmptyStateText_AdHocScopeWithoutUnassignedTabs_ExplainsWhyScopeIsEmpty()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);

        vm.ShowAdHocTabsCommand.Execute(null);

        Assert.True(vm.IsAdHocScopeActive);
        Assert.True(vm.IsCurrentScopeEmpty);
        Assert.True(vm.ShouldShowEmptyState);
        Assert.Equal("No Ad Hoc tabs. Open a file that is not assigned to a dashboard, or select a dashboard on the left.", vm.EmptyStateText);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public async Task OpenFilePathAsync_DashboardOwnedFileFromAdHoc_SwitchesToContainingDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\assigned.log");
        var tab = vm.Tabs.Single();

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];
        dashboard.Name = "Payments";
        dashboard.Model.FileIds.Add(tab.FileId);

        vm.ShowAdHocTabsCommand.Execute(null);

        await vm.OpenFilePathAsync(tab.FilePath);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.True(dashboard.IsSelected);
        Assert.False(vm.IsAdHocScopeActive);
        Assert.Same(tab, vm.SelectedTab);
        Assert.Contains(vm.SelectedTab!, filtered);
        Assert.Single(filtered);
        Assert.Contains(tab, filtered);
        Assert.False(vm.IsCurrentScopeEmpty);
        Assert.Equal("\"Payments\" has no open tabs. Open files from the dashboard tree, or switch back to Ad Hoc.", vm.EmptyStateText);
    }

    [Fact]
    public async Task OpenFilePathAsync_DashboardOwnedFileFromAdHoc_ReplacesAdHocEmptyState()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\assigned.log");
        var tab = vm.Tabs.Single();

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];
        dashboard.Name = "Payments";
        dashboard.Model.FileIds.Add(tab.FileId);

        vm.ShowAdHocTabsCommand.Execute(null);
        Assert.Equal("No Ad Hoc tabs. Open a file that is not assigned to a dashboard, or select a dashboard on the left.", vm.EmptyStateText);

        await vm.OpenFilePathAsync(tab.FilePath);

        Assert.Equal("Payments", vm.CurrentScopeLabel);
        Assert.Equal("\"Payments\" has no open tabs. Open files from the dashboard tree, or switch back to Ad Hoc.", vm.EmptyStateText);
    }

    [Fact]
    public async Task OpenFilePathAsync_AlreadyOpenHiddenDashboardTab_SwitchesToContainingDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboardA = vm.Groups[0];
        var dashboardB = vm.Groups[1];
        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        dashboardA.Model.FileIds.Add(tabA.FileId);
        dashboardB.Model.FileIds.Add(tabB.FileId);

        vm.ToggleGroupSelection(dashboardA);
        Assert.Equal(dashboardA.Id, vm.ActiveDashboardId);
        Assert.DoesNotContain(tabB, vm.FilteredTabs);

        await vm.OpenFilePathAsync(tabB.FilePath);

        Assert.Equal(dashboardB.Id, vm.ActiveDashboardId);
        Assert.True(dashboardB.IsSelected);
        Assert.False(dashboardA.IsSelected);
        Assert.Same(tabB, vm.SelectedTab);
        Assert.Contains(vm.SelectedTab!, vm.FilteredTabs);
    }

    [Fact]
    public async Task OpenFilePathAsync_UnassignedFileFromDashboardState_EndsInAdHocScope()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\assigned.log");
        await vm.OpenFilePathAsync(@"C:\test\adhoc.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        var assignedTab = vm.Tabs.First(t => t.FilePath == @"C:\test\assigned.log");
        var adhocTab = vm.Tabs.First(t => t.FilePath == @"C:\test\adhoc.log");
        dashboard.Model.FileIds.Add(assignedTab.FileId);

        vm.ToggleGroupSelection(dashboard);
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.DoesNotContain(adhocTab, vm.FilteredTabs);

        await vm.OpenFilePathAsync(adhocTab.FilePath);

        Assert.Null(vm.ActiveDashboardId);
        Assert.All(vm.Groups, g => Assert.False(g.IsSelected));
        Assert.True(vm.IsAdHocScopeActive);
        Assert.Same(adhocTab, vm.SelectedTab);
        Assert.Contains(vm.SelectedTab!, vm.FilteredTabs);
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

        Assert.False(status.Succeeded);
        Assert.Equal("Invalid timestamp. Use ISO-8601, yyyy-MM-dd HH:mm:ss, or HH:mm:ss.fff.", status.ErrorText);
        Assert.NotNull(vm.SelectedTab);
        Assert.True(vm.GlobalAutoScrollEnabled);
        Assert.True(vm.SelectedTab!.AutoScrollEnabled);
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

            Assert.True(status.Succeeded);
            Assert.Equal(string.Empty, status.ErrorText);
            Assert.NotNull(vm.SelectedTab);
            Assert.Equal(2, vm.SelectedTab!.NavigateToLineNumber);
            Assert.False(vm.GlobalAutoScrollEnabled);
            Assert.All(vm.Tabs, tab => Assert.False(tab.AutoScrollEnabled));
            Assert.Contains("exact timestamp match", vm.SelectedTab.StatusText, StringComparison.OrdinalIgnoreCase);
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

            Assert.True(status.Succeeded);
            Assert.Equal(string.Empty, status.ErrorText);
            Assert.NotNull(vm.SelectedTab);
            Assert.Equal(2, vm.SelectedTab!.NavigateToLineNumber);
            Assert.False(vm.GlobalAutoScrollEnabled);
            Assert.All(vm.Tabs, tab => Assert.False(tab.AutoScrollEnabled));
            Assert.Contains("no exact timestamp match", vm.SelectedTab.StatusText, StringComparison.OrdinalIgnoreCase);
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

            Assert.False(status.Succeeded);
            Assert.Equal("No parseable timestamps found in the current file.", status.ErrorText);
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

        Assert.True(status.Succeeded);
        Assert.Equal(string.Empty, status.ErrorText);
        Assert.NotNull(vm.SelectedTab);
        Assert.Equal(42, vm.SelectedTab!.NavigateToLineNumber);
        Assert.False(vm.GlobalAutoScrollEnabled);
        Assert.All(vm.Tabs, tab => Assert.False(tab.AutoScrollEnabled));
        Assert.Equal("Navigated to line 42.", vm.SelectedTab.StatusText);
    }

    [Fact]
    public async Task NavigateToLineAsync_StringInput_InvalidValue_ReturnsValidationMessage()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\line-target.log");

        var status = await vm.NavigateToLineAsync("abc");

        Assert.False(status.Succeeded);
        Assert.Equal("Invalid line number. Enter a whole number greater than 0.", status.ErrorText);
        Assert.NotNull(vm.SelectedTab);
        Assert.True(vm.GlobalAutoScrollEnabled);
        Assert.True(vm.SelectedTab!.AutoScrollEnabled);
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
    public async Task FilterPanel_StatusText_TracksSelectedTabFilterStatusUpdates()
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

        vm.SelectedTab.StatusText = "Filter active (tailing): 3 matching lines.";

        Assert.Equal("Filter active (tailing): 3 matching lines.", vm.FilterPanel.StatusText);
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
    public async Task GlobalAutoScrollEnabled_WhenEnabled_JumpsAllTabsToLogicalBottom()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tabA = vm.Tabs.First(tab => tab.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(tab => tab.FilePath == @"C:\test\b.log");

        vm.GlobalAutoScrollEnabled = false;
        await tabA.ApplyFilterAsync(
            Enumerable.Range(1, 120).ToArray(),
            statusText: "Filter active: 120 matching lines.");
        tabA.ScrollPosition = 10;
        tabB.ScrollPosition = 25;

        vm.GlobalAutoScrollEnabled = true;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((tabA.ScrollPosition != tabA.MaxScrollPosition || tabB.ScrollPosition != tabB.MaxScrollPosition) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(vm.GlobalAutoScrollEnabled);
        Assert.True(tabA.AutoScrollEnabled);
        Assert.True(tabB.AutoScrollEnabled);
        Assert.Equal(tabA.MaxScrollPosition, tabA.ScrollPosition);
        Assert.Equal(tabB.MaxScrollPosition, tabB.ScrollPosition);
        Assert.Equal(1000, tabA.ScrollBarValue);
        Assert.Equal(1000, tabB.ScrollBarValue);
        Assert.Equal(1000, tabA.ScrollBarMaximum);
        Assert.Equal(1000, tabB.ScrollBarMaximum);
        Assert.Equal(100, tabA.ScrollBarViewportSize);
        Assert.Equal(100, tabB.ScrollBarViewportSize);
    }

    [Fact]
    public async Task NavigateToLineAsync_DisableAutoScroll_DisablesGlobalAutoScroll()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tabA = vm.Tabs.First(tab => tab.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(tab => tab.FilePath == @"C:\test\b.log");

        await vm.NavigateToLineAsync(tabB.FilePath, 42, disableAutoScroll: true);

        Assert.False(vm.GlobalAutoScrollEnabled);
        Assert.False(tabA.AutoScrollEnabled);
        Assert.False(tabB.AutoScrollEnabled);
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
        vm.ToggleGroupSelection(g2);

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
    public async Task ReorderDashboardFileAsync_WhenDashboardIsActive_UpdatesFilteredTabAndSearchOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        dashboard.Model.FileIds.AddRange(new[]
        {
            vm.Tabs[0].FileId,
            vm.Tabs[1].FileId,
            vm.Tabs[2].FileId
        });

        vm.ToggleGroupSelection(dashboard);

        Assert.Equal(
            new[] { @"C:\test\a.log", @"C:\test\b.log", @"C:\test\c.log" },
            vm.FilteredTabs.Select(tab => tab.FilePath).ToArray());

        await vm.ReorderDashboardFileAsync(dashboard, vm.Tabs[2].FileId, vm.Tabs[0].FileId, DropPlacement.Before);

        Assert.Equal(
            new[] { vm.Tabs[2].FileId, vm.Tabs[0].FileId, vm.Tabs[1].FileId },
            dashboard.Model.FileIds);
        Assert.Equal(
            new[] { vm.Tabs[2].FileId, vm.Tabs[0].FileId, vm.Tabs[1].FileId },
            dashboard.MemberFiles.Select(member => member.FileId).ToArray());
        Assert.Equal(
            new[] { @"C:\test\c.log", @"C:\test\a.log", @"C:\test\b.log" },
            vm.FilteredTabs.Select(tab => tab.FilePath).ToArray());
        Assert.Equal(
            new[] { @"C:\test\c.log", @"C:\test\a.log", @"C:\test\b.log" },
            vm.GetSearchResultFileOrderSnapshot().ToArray());
    }

    [Fact]
    public async Task MoveDashboardFileAsync_WhenTargetDashboardIsActive_UpdatesFilteredTabAndSearchOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var source = vm.Groups[0];
        var target = vm.Groups[1];
        source.Model.FileIds.Add(vm.Tabs[0].FileId);
        target.Model.FileIds.Add(vm.Tabs[1].FileId);
        target.Model.FileIds.Add(vm.Tabs[2].FileId);

        vm.ToggleGroupSelection(target);

        await vm.MoveDashboardFileAsync(source, target, vm.Tabs[0].FileId, vm.Tabs[2].FileId, DropPlacement.Before);

        Assert.Empty(source.Model.FileIds);
        Assert.Equal(
            new[] { vm.Tabs[1].FileId, vm.Tabs[0].FileId, vm.Tabs[2].FileId },
            target.Model.FileIds);
        Assert.Equal(
            new[] { vm.Tabs[1].FileId, vm.Tabs[0].FileId, vm.Tabs[2].FileId },
            target.MemberFiles.Select(member => member.FileId).ToArray());
        Assert.Equal(
            new[] { @"C:\test\b.log", @"C:\test\a.log", @"C:\test\c.log" },
            vm.FilteredTabs.Select(tab => tab.FilePath).ToArray());
        Assert.Equal(
            new[] { @"C:\test\b.log", @"C:\test\a.log", @"C:\test\c.log" },
            vm.GetSearchResultFileOrderSnapshot().ToArray());
    }

    [Fact]
    public async Task OpenGroupFilesAsync_SelectingAnotherDashboard_CancelsPreviousLoad()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmDashboardCancel_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var slowPath = Path.Combine(testDir, "slow.log");
            var fastPath = Path.Combine(testDir, "fast.log");
            await File.WriteAllTextAsync(slowPath, "slow");
            await File.WriteAllTextAsync(fastPath, "fast");

            var slowEntry = new LogFileEntry { FilePath = slowPath };
            var fastEntry = new LogFileEntry { FilePath = fastPath };
            await fileRepo.AddAsync(slowEntry);
            await fileRepo.AddAsync(fastEntry);

            var logReader = new BlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var slowDashboard = vm.Groups[0];
            var fastDashboard = vm.Groups[1];
            slowDashboard.Model.FileIds.Add(slowEntry.Id);
            fastDashboard.Model.FileIds.Add(fastEntry.Id);

            vm.ToggleGroupSelection(slowDashboard);
            var slowLoadTask = vm.OpenGroupFilesAsync(slowDashboard);
            await logReader.WaitForBlockedBuildAsync();

            vm.ToggleGroupSelection(fastDashboard);
            var fastLoadTask = vm.OpenGroupFilesAsync(fastDashboard);

            await fastLoadTask;
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(logReader.BlockedBuildCanceled);
            Assert.Equal(fastDashboard.Id, vm.ActiveDashboardId);
            Assert.Single(vm.Tabs);
            Assert.Equal(fastPath, vm.Tabs[0].FilePath);
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenFilePathAsync_DuringSuppressedDashboardLoad_HidesEmptyStateOnceTabIsSelected()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmEmptyState_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var slowPath = Path.Combine(testDir, "slow.log");
            var fastPath = Path.Combine(testDir, "fast.log");
            await File.WriteAllTextAsync(slowPath, "slow");
            await File.WriteAllTextAsync(fastPath, "fast");

            var slowEntry = new LogFileEntry { FilePath = slowPath };
            var fastEntry = new LogFileEntry { FilePath = fastPath };
            await fileRepo.AddAsync(slowEntry);
            await fileRepo.AddAsync(fastEntry);

            var logReader = new BlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(slowEntry.Id);
            dashboard.Model.FileIds.Add(fastEntry.Id);

            vm.ToggleGroupSelection(dashboard);
            var slowLoadTask = vm.OpenGroupFilesAsync(dashboard);
            await logReader.WaitForBlockedBuildAsync();

            Assert.Null(vm.SelectedTab);
            Assert.True(vm.ShouldShowEmptyState);

            await vm.OpenFilePathAsync(fastPath);

            Assert.NotNull(vm.SelectedTab);
            Assert.Equal(fastPath, vm.SelectedTab!.FilePath);
            Assert.False(vm.ShouldShowEmptyState);

            vm.ShowAdHocTabsCommand.Execute(null);
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenGroupFilesAsync_SelectingBranch_CancelsPreviousLoad()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmBranchCancel_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var slowPath = Path.Combine(testDir, "slow.log");
            await File.WriteAllTextAsync(slowPath, "slow");

            var slowEntry = new LogFileEntry { FilePath = slowPath };
            await fileRepo.AddAsync(slowEntry);

            var logReader = new BlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);
            await vm.CreateContainerGroupCommand.ExecuteAsync(null);

            var slowDashboard = vm.Groups.First(group => group.Kind == LogGroupKind.Dashboard);
            var branch = vm.Groups.First(group => group.Kind == LogGroupKind.Branch);
            slowDashboard.Model.FileIds.Add(slowEntry.Id);

            vm.ToggleGroupSelection(slowDashboard);
            var slowLoadTask = vm.OpenGroupFilesAsync(slowDashboard);
            await logReader.WaitForBlockedBuildAsync();

            vm.ToggleGroupSelection(branch);

            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(logReader.BlockedBuildCanceled);
            Assert.Null(vm.ActiveDashboardId);
            Assert.Empty(vm.Tabs);
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteGroupCommand_DeletingActiveDashboard_CancelsPreviousLoad()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var messageBoxService = new StubMessageBoxService
        {
            OnShow = static (_, _, _, _) => MessageBoxResult.Yes
        };
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmDeleteCancel_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var slowPath = Path.Combine(testDir, "slow.log");
            await File.WriteAllTextAsync(slowPath, "slow");

            var slowEntry = new LogFileEntry { FilePath = slowPath };
            await fileRepo.AddAsync(slowEntry);

            var logReader = new BlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService,
                messageBoxService: messageBoxService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var slowDashboard = Assert.Single(vm.Groups);
            slowDashboard.Model.FileIds.Add(slowEntry.Id);

            vm.ToggleGroupSelection(slowDashboard);
            var slowLoadTask = vm.OpenGroupFilesAsync(slowDashboard);
            await logReader.WaitForBlockedBuildAsync();

            await vm.DeleteGroupCommand.ExecuteAsync(slowDashboard);
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(logReader.BlockedBuildCanceled);
            Assert.Null(vm.ActiveDashboardId);
            Assert.Empty(vm.Tabs);
            Assert.Empty(vm.Groups);
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteGroupCommand_WhenUserDeclinesConfirmation_KeepsDashboard()
    {
        var messageBoxService = new StubMessageBoxService
        {
            OnShow = (message, caption, buttons, image) =>
            {
                Assert.Contains("Delete the dashboard", message);
                Assert.Contains("does not delete any log files from disk", message);
                Assert.Equal("Delete Dashboard?", caption);
                Assert.Equal(MessageBoxButton.YesNo, buttons);
                Assert.Equal(MessageBoxImage.Warning, image);
                return MessageBoxResult.No;
            }
        };
        var vm = CreateViewModel(messageBoxService: messageBoxService);
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);

        await vm.DeleteGroupCommand.ExecuteAsync(dashboard);

        Assert.Single(vm.Groups);
        Assert.Equal(dashboard.Id, vm.Groups[0].Id);
    }

    [Fact]
    public async Task ImportViewCommand_ReplacingActiveView_CancelsPreviousLoad()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = CreateImportedView()
        };
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmImportCancel_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var slowPath = Path.Combine(testDir, "slow.log");
            await File.WriteAllTextAsync(slowPath, "slow");

            var slowEntry = new LogFileEntry { FilePath = slowPath };
            await fileRepo.AddAsync(slowEntry);
            await groupRepo.AddAsync(new LogGroup
            {
                Name = "Current Dashboard",
                Kind = LogGroupKind.Dashboard,
                FileIds = new List<string> { slowEntry.Id }
            });

            var fileDialogService = new StubFileDialogService
            {
                OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { @"C:\views\incoming-view.json" })
            };
            var messageBoxService = new StubMessageBoxService
            {
                OnShow = (_, _, _, _) => MessageBoxResult.No
            };
            var logReader = new BlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                groupRepo: groupRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService,
                fileDialogService: fileDialogService,
                messageBoxService: messageBoxService);

            await vm.InitializeAsync();
            var currentDashboard = Assert.Single(vm.Groups);

            vm.ToggleGroupSelection(currentDashboard);
            var slowLoadTask = vm.OpenGroupFilesAsync(currentDashboard);
            await logReader.WaitForBlockedBuildAsync();

            await vm.ImportViewCommand.ExecuteAsync(null);
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            await Task.Run(() => SpinWait.SpinUntil(() => logReader.BlockedBuildCanceled, TimeSpan.FromSeconds(5)));
            Assert.True(logReader.BlockedBuildCanceled);
            Assert.Null(vm.ActiveDashboardId);
            Assert.True(vm.IsAdHocScopeActive);
            Assert.Empty(vm.Tabs);
            Assert.False(vm.IsDashboardLoading);
            Assert.Equal(new[] { "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
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

        var openOrder = GetOpenOrderMap(vm);
        var pinOrder = GetPinOrderMap(vm);
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
    public async Task ImportViewCommand_WhenUserChoosesExport_ExportsCurrentViewBeforeApplyingImport()
    {
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = CreateImportedView()
        };
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        const string importPath = @"C:\views\incoming-view.json";
        const string exportPath = @"C:\views\backup-view.json";
        var promptCount = 0;
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { importPath }),
            OnShowSaveFileDialog = _ => new SaveFileDialogResult(true, exportPath)
        };
        var messageBoxService = new StubMessageBoxService
        {
            OnShow = (message, caption, buttons, image) =>
            {
                promptCount++;
                Assert.Contains("replace your current dashboard view", message);
                Assert.Equal("Export Current View?", caption);
                Assert.Equal(MessageBoxButton.YesNoCancel, buttons);
                Assert.Equal(MessageBoxImage.Warning, image);
                return MessageBoxResult.Yes;
            }
        };

        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        await vm.ImportViewCommand.ExecuteAsync(null);

        Assert.Equal(1, promptCount);
        Assert.Equal(importPath, groupRepo.LastImportPath);
        Assert.Equal(exportPath, groupRepo.LastExportPath);
        Assert.Equal(1, groupRepo.ExportCallCount);
        Assert.Equal(new[] { "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());

        var exportIndex = groupRepo.CallSequence.IndexOf($"Export:{exportPath}");
        var replaceAllIndex = groupRepo.CallSequence.IndexOf("ReplaceAll");
        Assert.True(exportIndex >= 0);
        Assert.True(replaceAllIndex > exportIndex);
    }

    [Fact]
    public async Task ImportViewCommand_WhenUserDeclinesExport_AppliesImportWithoutSavingCurrentView()
    {
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = CreateImportedView()
        };
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var saveDialogShown = false;
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { @"C:\views\incoming-view.json" }),
            OnShowSaveFileDialog = _ =>
            {
                saveDialogShown = true;
                return new SaveFileDialogResult(true, @"C:\views\backup-view.json");
            }
        };
        var messageBoxService = new StubMessageBoxService
        {
            OnShow = (_, _, _, _) => MessageBoxResult.No
        };
        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        await vm.ImportViewCommand.ExecuteAsync(null);

        Assert.False(saveDialogShown);
        Assert.Null(groupRepo.LastExportPath);
        Assert.Equal(new[] { "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task ImportViewCommand_WhenExportIsCancelled_KeepsCurrentView()
    {
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = CreateImportedView()
        };
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { @"C:\views\incoming-view.json" }),
            OnShowSaveFileDialog = _ => new SaveFileDialogResult(false, null)
        };
        var messageBoxService = new StubMessageBoxService
        {
            OnShow = (_, _, _, _) => MessageBoxResult.Yes
        };
        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        await vm.ImportViewCommand.ExecuteAsync(null);

        Assert.Equal(0, groupRepo.ExportCallCount);
        Assert.Equal(new[] { "Current Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task ImportViewCommand_WhenNoCurrentViewExists_SkipsExportPrompt()
    {
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = CreateImportedView()
        };

        var promptShown = false;
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { @"C:\views\incoming-view.json" })
        };
        var messageBoxService = new StubMessageBoxService
        {
            OnShow = (_, _, _, _) =>
            {
                promptShown = true;
                return MessageBoxResult.Yes;
            }
        };
        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        await vm.ImportViewCommand.ExecuteAsync(null);

        Assert.False(promptShown);
        Assert.Equal(new[] { "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task ImportViewCommand_WhenImportedViewUsesOnlyLocalAbsolutePaths_DoesNotShowNonLocalPathWarning()
    {
        const string importPath = @"C:\views\incoming-view.json";
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = CreateImportedView(filePaths: [@"C:\logs\local.log"])
        };
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { importPath })
        };
        var messageBoxService = new StubMessageBoxService();
        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        await vm.ImportViewCommand.ExecuteAsync(null);

        Assert.Null(messageBoxService.LastCaption);
        Assert.Equal(new[] { "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
        Assert.Equal(new[] { $"Import:{importPath}", "ReplaceAll" }, groupRepo.CallSequence.ToArray());
    }

    [Fact]
    public async Task ImportViewCommand_WhenImportedViewContainsUncPath_DoesNotShowNonLocalPathWarning()
    {
        const string importPath = @"C:\views\incoming-view.json";
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = CreateImportedView(filePaths: [@"\\server\share\app.log"])
        };
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { importPath })
        };
        var messageBoxService = new StubMessageBoxService
        {
            OnShow = (_, _, _, _) => MessageBoxResult.No
        };
        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        await vm.ImportViewCommand.ExecuteAsync(null);

        Assert.Equal("Export Current View?", messageBoxService.LastCaption);
        Assert.Equal(new[] { "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
        Assert.Equal(new[] { "Add:Current Dashboard", $"Import:{importPath}", "ReplaceAll" }, groupRepo.CallSequence.ToArray());
        Assert.Equal(0, groupRepo.ExportCallCount);
    }

    [Theory]
    [InlineData(@"logs\relative.log")]
    [InlineData(@"C:logs\drive-relative.log")]
    public async Task ImportViewCommand_WhenImportedViewContainsSuspiciousPath_DecliningTrustWarning_KeepsCurrentView(string suspiciousPath)
    {
        const string importPath = @"C:\views\incoming-view.json";
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = CreateImportedView(filePaths: [suspiciousPath])
        };
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var promptCount = 0;
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { importPath })
        };
        var messageBoxService = new StubMessageBoxService
        {
            OnShow = (message, caption, buttons, image) =>
            {
                promptCount++;
                Assert.Equal("Import Non-Local Paths?", caption);
                Assert.Equal(MessageBoxButton.YesNo, buttons);
                Assert.Equal(MessageBoxImage.Warning, image);
                Assert.Contains(suspiciousPath, message, StringComparison.Ordinal);
                return MessageBoxResult.No;
            }
        };
        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        await vm.ImportViewCommand.ExecuteAsync(null);

        Assert.Equal(1, promptCount);
        Assert.Equal(new[] { "Current Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
        Assert.Equal(new[] { "Add:Current Dashboard", $"Import:{importPath}" }, groupRepo.CallSequence.ToArray());
        Assert.Equal(0, groupRepo.ExportCallCount);
    }

    [Fact]
    public async Task ImportViewCommand_WhenImportedViewIsInvalid_KeepsCurrentViewAndShowsError()
    {
        var groupRepo = new RecordingImportExportLogGroupRepository
        {
            ImportResult = new ViewExport
            {
                Groups = new List<ViewExportGroup>
                {
                    new()
                    {
                        Id = "branch-1",
                        Name = "Broken Folder",
                        Kind = LogGroupKind.Branch,
                        FilePaths = new List<string> { @"C:\logs\should-not-import.log" }
                    }
                }
            }
        };
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { @"C:\views\incoming-view.json" })
        };
        var messageBoxService = new StubMessageBoxService();
        var vm = CreateViewModel(groupRepo: groupRepo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        await vm.ImportViewCommand.ExecuteAsync(null);

        Assert.Equal("Import Failed", messageBoxService.LastCaption);
        Assert.Contains("cannot own file paths", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Current Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
        Assert.Equal(new[] { "Add:Current Dashboard", "Import:C:\\views\\incoming-view.json" }, groupRepo.CallSequence.ToArray());
    }

    [Fact]
    public async Task OpenFilePathAsync_WhenLogFilesStoreBecomesInvalidAfterStartup_RecoversAndRetries()
    {
        var fileRepo = new JsonLogFileRepository();
        var messageBoxService = new StubMessageBoxService();
        var vm = CreateViewModel(fileRepo: fileRepo, messageBoxService: messageBoxService);
        await vm.InitializeAsync();

        WriteInvalidStoreFile("logfiles.json");

        await vm.OpenFilePathAsync(@"C:\logs\recovered.log");

        var openedTab = Assert.Single(vm.Tabs);
        Assert.Equal(@"C:\logs\recovered.log", openedTab.FilePath);
        Assert.Equal("LogReader Recovered Saved Data", messageBoxService.LastCaption);
        Assert.Contains("retried your action", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(AppPaths.DataDirectory, "logfiles.corrupt-*.json"));

        var storedEntry = Assert.Single(await fileRepo.GetAllAsync());
        Assert.Equal(@"C:\logs\recovered.log", storedEntry.FilePath);
    }

    [Fact]
    public async Task CreateGroupCommand_WhenLogGroupsStoreBecomesInvalidAfterStartup_RecoversAndRetries()
    {
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 0
        });

        var messageBoxService = new StubMessageBoxService();
        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo, messageBoxService: messageBoxService);
        await vm.InitializeAsync();
        vm.ToggleGroupSelection(Assert.Single(vm.Groups));

        WriteInvalidStoreFile("loggroups.json");

        await vm.CreateGroupCommand.ExecuteAsync(null);

        Assert.Equal("LogReader Recovered Saved Data", messageBoxService.LastCaption);
        Assert.True(vm.IsAdHocScopeActive);
        Assert.Null(vm.ActiveDashboardId);

        var createdGroup = Assert.Single(vm.Groups);
        Assert.Equal("New Dashboard", createdGroup.Name);

        var persistedGroup = Assert.Single(await groupRepo.GetAllAsync());
        Assert.Equal("New Dashboard", persistedGroup.Name);
    }

    [Fact]
    public async Task OpenFilePathAsync_WhenRecoveredStoreFailsAgain_ShowsFriendlyRecoveryError()
    {
        var fileRepo = new JsonLogFileRepository();
        var recoveryCoordinator = new StubPersistedStateRecoveryCoordinator();
        recoveryCoordinator.OnRecover = exception => new PersistedStateRecoveryResult(
            exception.StoreDisplayName,
            exception.StorePath,
            exception.StorePath + ".backup",
            exception.StorePath + ".backup.note.txt",
            exception.FailureReason);

        var messageBoxService = new StubMessageBoxService();
        var vm = CreateViewModel(
            fileRepo: fileRepo,
            messageBoxService: messageBoxService,
            persistedStateRecoveryCoordinator: recoveryCoordinator);
        await vm.InitializeAsync();

        WriteInvalidStoreFile("logfiles.json");

        await vm.OpenFilePathAsync(@"C:\logs\still-broken.log");

        Assert.Empty(vm.Tabs);
        Assert.Equal(1, recoveryCoordinator.CallCount);
        Assert.Equal("LogReader Recovery Failed", messageBoxService.LastCaption);
        Assert.Contains("could not recover", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logfiles.json", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
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
    public async Task ApplyImportedViewAsync_WhenExistingTabsAreOpen_LeavesThemInAdHocScope()
    {
        var fileRepo = new StubLogFileRepository();
        var existingEntry = new LogFileEntry { FilePath = @"C:\logs\kept-open.log" };
        await fileRepo.AddAsync(existingEntry);

        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { existingEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        var currentDashboard = Assert.Single(vm.Groups);
        vm.ToggleGroupSelection(currentDashboard);
        await vm.OpenFilePathAsync(existingEntry.FilePath);

        await vm.ApplyImportedViewAsync(CreateImportedView());

        var keptTab = Assert.Single(vm.Tabs);
        Assert.Equal(existingEntry.FilePath, keptTab.FilePath);
        Assert.True(vm.IsAdHocScopeActive);
        Assert.Null(vm.ActiveDashboardId);
        Assert.Equal(existingEntry.FilePath, Assert.Single(vm.FilteredTabs).FilePath);
        Assert.Equal(new[] { "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task ApplyImportedViewAsync_DoesNotReReadGroupsAfterReplace()
    {
        var groupRepo = new ThrowOnGetAfterReplaceLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Current Dashboard",
            Kind = LogGroupKind.Dashboard
        });

        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.ApplyImportedViewAsync(CreateImportedView());

        Assert.Equal(1, groupRepo.ReplaceAllCallCount);
        Assert.Equal(new[] { "Imported Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
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

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 4000, int pollIntervalMs = 25)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition() && (DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            await Task.Delay(pollIntervalMs);

        Assert.True(condition(), "Timed out waiting for condition.");
    }
}
