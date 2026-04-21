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
using System.ComponentModel;
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
    private const string FilterCurrentTabClearedStatusText = "Filter output cleared because the selected tab changed. Reapply filter to refresh.";
    private const string FilterCurrentTabStaleStatusText = "Filter output is for a previous tab in this scope. Reapply filter to refresh.";
    private const string FilterAllOpenTabsStaleStatusText = "Filter output is for a previous set of open tabs. Reapply filter to refresh.";
    private const string FilterSourceModeStaleStatusText = "Filter output is for a different source mode. Reapply filter to refresh.";

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
        public IReadOnlyList<SearchResult> NextResults { get; set; } = Array.Empty<SearchResult>();
        public int SearchFileCallCount { get; private set; }
        public int SearchFilesCallCount { get; private set; }
        public SearchRequest? LastSearchFileRequest { get; private set; }
        public SearchRequest? LastSearchFilesRequest { get; private set; }
        public IDictionary<string, FileEncoding>? LastSearchFilesEncodings { get; private set; }
        public Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>>? SearchFileAsyncHandler { get; set; }
        public Func<SearchRequest, IDictionary<string, FileEncoding>, CancellationToken, Task<IReadOnlyList<SearchResult>>>? SearchFilesAsyncHandler { get; set; }

        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        {
            SearchFileCallCount++;
            LastSearchFileRequest = new SearchRequest
            {
                Query = request.Query,
                IsRegex = request.IsRegex,
                CaseSensitive = request.CaseSensitive,
                FilePaths = request.FilePaths.ToList(),
                AllowedLineNumbersByFilePath = request.AllowedLineNumbersByFilePath.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase),
                StartLineNumber = request.StartLineNumber,
                EndLineNumber = request.EndLineNumber,
                FromTimestamp = request.FromTimestamp,
                ToTimestamp = request.ToTimestamp,
                SourceMode = request.SourceMode
            };
            if (SearchFileAsyncHandler != null)
                return SearchFileAsyncHandler(filePath, request, encoding, ct);

            return Task.FromResult(new SearchResult
            {
                FilePath = NextResult.FilePath,
                Hits = NextResult.Hits.ToList(),
                Error = NextResult.Error,
                HasParseableTimestamps = NextResult.HasParseableTimestamps
            });
        }

        public Task<SearchResult> SearchFileRangeAsync(
            string filePath,
            SearchRequest request,
            FileEncoding encoding,
            Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
            CancellationToken ct = default)
            => SearchFileAsync(filePath, request, encoding, ct);

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
        {
            SearchFilesCallCount++;
            LastSearchFilesRequest = new SearchRequest
            {
                Query = request.Query,
                IsRegex = request.IsRegex,
                CaseSensitive = request.CaseSensitive,
                FilePaths = request.FilePaths.ToList(),
                AllowedLineNumbersByFilePath = request.AllowedLineNumbersByFilePath.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase),
                StartLineNumber = request.StartLineNumber,
                EndLineNumber = request.EndLineNumber,
                FromTimestamp = request.FromTimestamp,
                ToTimestamp = request.ToTimestamp,
                SourceMode = request.SourceMode
            };
            LastSearchFilesEncodings = new Dictionary<string, FileEncoding>(fileEncodings, StringComparer.OrdinalIgnoreCase);
            if (SearchFilesAsyncHandler != null)
                return SearchFilesAsyncHandler(request, fileEncodings, ct);

            return Task.FromResult<IReadOnlyList<SearchResult>>(NextResults
                .Select(result => new SearchResult
                {
                    FilePath = result.FilePath,
                    Hits = result.Hits.ToList(),
                    Error = result.Error,
                    HasParseableTimestamps = result.HasParseableTimestamps
                })
                .ToList());
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

    private sealed class ArmableBlockingLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries = new();
        private TaskCompletionSource<bool> _blockedGetByPathsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _releaseBlockedGetByPaths = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isBlockingGetByPathsArmed;

        public void ArmGetByPathsBlocking()
        {
            _blockedGetByPathsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseBlockedGetByPaths = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _isBlockingGetByPathsArmed, 1);
        }

        public Task WaitForBlockedGetByPathsAsync()
            => _blockedGetByPathsStarted.Task;

        public void ReleaseBlockedGetByPaths()
        {
            Interlocked.Exchange(ref _isBlockingGetByPathsArmed, 0);
            _releaseBlockedGetByPaths.TrySetResult(true);
        }

        public Task<List<LogFileEntry>> GetAllAsync()
            => Task.FromResult(_entries.ToList());

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
                _entries
                    .Where(entry => idSet.Contains(entry.Id))
                    .ToDictionary(entry => entry.Id, StringComparer.Ordinal));
        }

        public async Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
        {
            if (Volatile.Read(ref _isBlockingGetByPathsArmed) != 0)
            {
                _blockedGetByPathsStarted.TrySetResult(true);
                await _releaseBlockedGetByPaths.Task;
            }

            var pathSet = filePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _entries
                .Where(entry => pathSet.Contains(entry.FilePath))
                .ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase);
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

    private sealed class ThrowingLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public bool ThrowOnGetAll { get; set; }

        public Task<List<LogGroup>> GetAllAsync()
        {
            if (ThrowOnGetAll)
                throw new IOException("Group refresh failed.");

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

        private static LogGroup CloneGroup(LogGroup group)
        {
            return new LogGroup
            {
                Id = group.Id,
                Name = group.Name,
                SortOrder = group.SortOrder,
                Kind = group.Kind,
                ParentGroupId = group.ParentGroupId,
                FileIds = group.FileIds.ToList()
            };
        }
    }

    private sealed class ArmableBlockingLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();
        private TaskCompletionSource<bool> _blockedGetAllStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _releaseBlockedGetAll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isBlockingArmed;

        public void ArmBlocking()
        {
            _blockedGetAllStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseBlockedGetAll = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _isBlockingArmed, 1);
        }

        public Task WaitForBlockedGetAllAsync()
            => _blockedGetAllStarted.Task;

        public void ReleaseBlockedGetAll()
        {
            Interlocked.Exchange(ref _isBlockingArmed, 0);
            _releaseBlockedGetAll.TrySetResult(true);
        }

        public async Task<List<LogGroup>> GetAllAsync()
        {
            if (Volatile.Read(ref _isBlockingArmed) != 0)
            {
                _blockedGetAllStarted.TrySetResult(true);
                await _releaseBlockedGetAll.Task;
            }

            return _groups.Select(CloneGroup).ToList();
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

    private sealed class ThrowingLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries = new();

        public bool ThrowOnGetByIds { get; set; }

        public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(_entries.ToList());

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        {
            if (ThrowOnGetByIds)
                throw new IOException("Member refresh failed.");

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

    private sealed class BlockingAppendableViewportRefreshLogReader : ILogReaderService
    {
        private readonly List<string> _lines;
        private readonly TaskCompletionSource<bool> _blockedReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseBlockedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNextRead;

        public BlockingAppendableViewportRefreshLogReader(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public Task BlockedReadStarted => _blockedReadStarted.Task;

        public void BlockNextRead() => Interlocked.Exchange(ref _blockNextRead, 1);

        public void ReleaseBlockedRead() => _releaseBlockedRead.TrySetResult(true);

        public void AppendLine(string line) => _lines.Add(line);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public async Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _blockNextRead, 0) == 1)
            {
                _blockedReadStarted.TrySetResult(true);
                await _releaseBlockedRead.Task.WaitAsync(ct);
            }

            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, _lines.Count - boundedStart));
            return _lines.Skip(boundedStart).Take(boundedCount).ToList();
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        {
            if (lineNumber < 0 || lineNumber >= _lines.Count)
                return Task.FromResult(string.Empty);

            return Task.FromResult(_lines[lineNumber]);
        }

        private LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = _lines.Count * 100
            };

            for (var i = 0; i < _lines.Count; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    private sealed class ReleasableBlockingLogReaderService : ILogReaderService
    {
        private readonly HashSet<string> _blockedPaths;
        private readonly TaskCompletionSource<bool> _blockedBuildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseBlockedBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _buildIndexCallCount;

        public ReleasableBlockingLogReaderService(params string[] blockedPaths)
        {
            _blockedPaths = blockedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public bool BlockedBuildCanceled { get; private set; }

        public int BuildIndexCallCount => Volatile.Read(ref _buildIndexCallCount);

        public Task WaitForBlockedBuildAsync()
            => _blockedBuildStarted.Task;

        public void ReleaseBlockedBuild()
            => _releaseBlockedBuild.TrySetResult(true);

        public async Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _buildIndexCallCount);
            if (_blockedPaths.Contains(filePath))
            {
                _blockedBuildStarted.TrySetResult(true);
                try
                {
                    await _releaseBlockedBuild.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    BlockedBuildCanceled = true;
                    throw;
                }
            }

            return CreateIndex(filePath);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            var lines = Enumerable.Range(startLine + 1, Math.Max(0, count))
                .Select(lineNumber => $"Line {lineNumber} content")
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1} content");

        private static LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = 200
            };

            index.LineOffsets.Add(0);
            index.LineOffsets.Add(100);
            return index;
        }
    }

    private sealed class ArmableBlockingLogReaderService : ILogReaderService
    {
        private readonly HashSet<string> _blockedPaths;
        private TaskCompletionSource<bool> _blockedBuildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _releaseBlockedBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isBlockingArmed;
        private int _buildIndexCallCount;

        public ArmableBlockingLogReaderService(params string[] blockedPaths)
        {
            _blockedPaths = blockedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public int BuildIndexCallCount => Volatile.Read(ref _buildIndexCallCount);

        public void ArmBlocking()
        {
            _blockedBuildStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseBlockedBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _isBlockingArmed, 1);
        }

        public Task WaitForBlockedBuildAsync()
            => _blockedBuildStarted.Task;

        public void ReleaseBlockedBuild()
        {
            Interlocked.Exchange(ref _isBlockingArmed, 0);
            _releaseBlockedBuild.TrySetResult(true);
        }

        public async Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _buildIndexCallCount);
            await WaitIfBlockedAsync(filePath, ct);

            return CreateIndex(filePath);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => UpdateIndexCoreAsync(filePath, existingIndex, ct);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            var lines = Enumerable.Range(startLine + 1, Math.Max(0, count))
                .Select(lineNumber => $"Line {lineNumber} content")
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1} content");

        private static LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = 200
            };

            index.LineOffsets.Add(0);
            index.LineOffsets.Add(100);
            return index;
        }

        private async Task<LineIndex> UpdateIndexCoreAsync(string filePath, LineIndex existingIndex, CancellationToken ct)
        {
            await WaitIfBlockedAsync(filePath, ct);
            return existingIndex;
        }

        private async Task WaitIfBlockedAsync(string filePath, CancellationToken ct)
        {
            if (Volatile.Read(ref _isBlockingArmed) != 0 &&
                _blockedPaths.Contains(filePath))
            {
                _blockedBuildStarted.TrySetResult(true);
                await _releaseBlockedBuild.Task.WaitAsync(ct);
            }
        }
    }

    private sealed class CoordinatedParallelLogReaderService : ILogReaderService
    {
        private readonly HashSet<string> _coordinatedPaths;
        private readonly TaskCompletionSource<bool> _targetConcurrencyReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseBuilds = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, byte> _attemptedPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _targetConcurrency;
        private int _currentConcurrency;
        private int _maxObservedConcurrency;

        public CoordinatedParallelLogReaderService(IEnumerable<string> coordinatedPaths, int targetConcurrency)
        {
            _coordinatedPaths = coordinatedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _targetConcurrency = targetConcurrency;
        }

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public IReadOnlyCollection<string> AttemptedPaths => _attemptedPaths.Keys.ToArray();

        public Task WaitForTargetConcurrencyAsync()
            => _targetConcurrencyReached.Task;

        public void ReleaseBuilds()
            => _releaseBuilds.TrySetResult(true);

        public async Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            _attemptedPaths.TryAdd(filePath, 0);
            if (_coordinatedPaths.Contains(filePath))
            {
                var concurrency = Interlocked.Increment(ref _currentConcurrency);
                UpdateMaxObservedConcurrency(concurrency);
                if (concurrency >= _targetConcurrency)
                    _targetConcurrencyReached.TrySetResult(true);

                try
                {
                    await _releaseBuilds.Task.WaitAsync(ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _currentConcurrency);
                }
            }

            return CreateIndex(filePath);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            var lines = Enumerable.Range(startLine + 1, Math.Max(0, count))
                .Select(lineNumber => $"Line {lineNumber} content")
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1} content");

        private void UpdateMaxObservedConcurrency(int concurrency)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxObservedConcurrency);
                if (concurrency <= observed)
                    return;

                if (Interlocked.CompareExchange(ref _maxObservedConcurrency, concurrency, observed) == observed)
                    return;
            }
        }

        private static LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = 200
            };

            index.LineOffsets.Add(0);
            index.LineOffsets.Add(100);
            return index;
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
        ISettingsRepository? settingsRepo = null,
        IFileTailService? tailService = null,
        ILogReaderService? logReader = null,
        ISearchService? searchService = null,
        IEncodingDetectionService? encodingDetectionService = null,
        IFileDialogService? fileDialogService = null,
        IMessageBoxService? messageBoxService = null,
        ISettingsDialogService? settingsDialogService = null,
        IBulkOpenPathsDialogService? bulkOpenPathsDialogService = null,
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory = null,
        IPersistedStateRecoveryCoordinator? persistedStateRecoveryCoordinator = null,
        bool enableLifecycleTimer = false,
        ILogAppearanceService? logAppearanceService = null,
        ITabLifecycleScheduler? tabLifecycleScheduler = null)
    {
        return new MainViewModel(
            fileRepo ?? new StubLogFileRepository(),
            groupRepo ?? new StubLogGroupRepository(),
            settingsRepo ?? new StubSettingsRepository(),
            logReader ?? new StubLogReaderService(),
            searchService ?? new StubSearchService(),
            tailService ?? new StubFileTailService(),
            encodingDetectionService ?? new FileEncodingDetectionService(),
            enableLifecycleTimer: enableLifecycleTimer,
            fileDialogService: fileDialogService,
            messageBoxService: messageBoxService,
            settingsDialogService: settingsDialogService,
            bulkOpenPathsDialogService: bulkOpenPathsDialogService,
            settingsViewModelFactory: settingsViewModelFactory,
            persistedStateRecoveryCoordinator: persistedStateRecoveryCoordinator,
            workspaceViewModelReference: null,
            logAppearanceService: logAppearanceService,
            tabLifecycleScheduler: tabLifecycleScheduler,
            fileCatalogService: null,
            tabWorkspace: null,
            dashboardWorkspace: null);
    }

    private static IReadOnlyDictionary<string, long> GetOpenOrderMap(MainViewModel vm) => vm.TabOpenOrder;

    private static IReadOnlyDictionary<string, long> GetPinOrderMap(MainViewModel vm) => vm.TabPinOrder;

    private static LogTabViewModel FindScopedTab(MainViewModel vm, string filePath, string? scopeDashboardId)
    {
        return vm.Tabs.Single(tab =>
            string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal));
    }

    private static IDashboardWorkspaceHost CreateDashboardHost(MainViewModel vm)
    {
        var viewModelReference = new MainViewModelReference();
        viewModelReference.Attach(vm);
        return new DashboardWorkspaceHostAdapter(viewModelReference);
    }

    private static void RefreshDashboardMemberFiles(LogGroupViewModel dashboard, params (string FileId, string FilePath)[] members)
    {
        dashboard.RefreshMemberFiles(
            Array.Empty<LogTabViewModel>(),
            members.ToDictionary(member => member.FileId, member => member.FilePath, StringComparer.Ordinal),
            members.ToDictionary(member => member.FileId, _ => true, StringComparer.Ordinal),
            selectedFileId: null,
            showFullPath: false);
    }

    private static async Task ChangeEncodingAndWaitForLoadAsync(LogTabViewModel tab, FileEncoding encoding)
    {
        var loadCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogTabViewModel.IsLoading) && !tab.IsLoading)
                loadCompleted.TrySetResult(true);
        }

        tab.PropertyChanged += OnPropertyChanged;
        try
        {
            tab.Encoding = encoding;
            if (!tab.IsLoading)
                loadCompleted.TrySetResult(true);

            await loadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            tab.PropertyChanged -= OnPropertyChanged;
        }
    }

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
    public async Task LogViewportView_TrySelectLine_ReplacesExistingMultiSelectionWithTargetLine()
    {
        await SingleThreadSynchronizationContext.RunAsync(() =>
        {
            var listBox = new ListBox
            {
                SelectionMode = SelectionMode.Extended,
                ItemsSource = new[]
                {
                    new LogLineViewModel { LineNumber = 10, Text = "ten" },
                    new LogLineViewModel { LineNumber = 20, Text = "twenty" },
                    new LogLineViewModel { LineNumber = 30, Text = "thirty" }
                }
            };

            listBox.ApplyTemplate();
            listBox.UpdateLayout();
            listBox.SelectedItems.Add(listBox.Items[0]);
            listBox.SelectedItems.Add(listBox.Items[1]);

            var selected = LogViewportView.TrySelectLine(listBox, 30);

            Assert.True(selected);
            Assert.Single(listBox.SelectedItems);
            Assert.Equal(30, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LogViewportView_TrySelectLine_ReturnsFalseWhenLineIsMissing()
    {
        await SingleThreadSynchronizationContext.RunAsync(() =>
        {
            var listBox = new ListBox
            {
                ItemsSource = new[]
                {
                    new LogLineViewModel { LineNumber = 10, Text = "ten" }
                }
            };

            listBox.ApplyTemplate();
            listBox.UpdateLayout();

            var selected = LogViewportView.TrySelectLine(listBox, 99);

            Assert.False(selected);
            Assert.Null(listBox.SelectedItem);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void TabStripView_ShouldRetrySelectedTabRealization_OnlyWhenNoRetryIsPendingForThatTab()
    {
        var tab = new LogTabViewModel(
            Guid.NewGuid().ToString("N"),
            @"C:\test\a.log",
            new StubLogReaderService(),
            new StubFileTailService(),
            new StubEncodingDetectionService(),
            new AppSettings());

        Assert.True(TabStripView.ShouldRetrySelectedTabRealization(null, tab));
        Assert.False(TabStripView.ShouldRetrySelectedTabRealization(tab.TabInstanceId, tab));
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
            OnShowDialog = settingsVm =>
            {
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

        await vm.OpenSettingsAsync();

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
            OnShowDialog = settingsVm =>
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

        await vm.OpenSettingsAsync();

        var savedPattern = Assert.Single(settingsRepo.Settings.DateRollingPatterns);
        Assert.Equal("Existing", savedPattern.Name);
    }

    [Fact]
    public async Task InitializeAndReloadSettings_UsesInjectedAppearanceService()
    {
        var appearanceService = new StubLogAppearanceService();
        var settingsRepo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                LogFontFamily = "Consolas"
            }
        };
        var settingsDialogService = new StubSettingsDialogService
        {
            OnShowDialog = settingsVm =>
            {
                settingsVm.LogFontFamily = "Cascadia Mono";
                return true;
            }
        };
        var vm = CreateViewModel(
            settingsRepo: settingsRepo,
            settingsDialogService: settingsDialogService,
            logAppearanceService: appearanceService);

        await vm.InitializeAsync();
        await vm.OpenSettingsAsync();

        Assert.Equal(2, appearanceService.ApplyCallCount);
        Assert.Equal("Cascadia Mono", appearanceService.LastSettings?.LogFontFamily);
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
    public async Task CloseTab_RecentReopen_RestoresScopeLocalStateWithoutRebuildingWarmSession()
    {
        var reader = new StubLogReaderService();
        var vm = CreateViewModel(logReader: reader);
        try
        {
            await vm.InitializeAsync();

            const string filePath = @"C:\test\recent.log";
            await vm.OpenFilePathAsync(filePath);

            var originalTab = Assert.Single(vm.Tabs);
            vm.TogglePinTab(originalTab);
            await ChangeEncodingAndWaitForLoadAsync(originalTab, FileEncoding.Utf16);
            vm.GlobalAutoScrollEnabled = false;
            await originalTab.ApplyFilterAsync(
                Enumerable.Range(1, 120).ToArray(),
                "Filter active: 120 matching lines.",
                new SearchRequest
                {
                    Query = "Line",
                    FilePaths = new List<string> { filePath }
                },
                hasParseableTimestamps: false);
            await originalTab.LoadViewportAsync(10, originalTab.ViewportLineCount);
            originalTab.SetNavigateTargetLine(42);

            var buildCountBeforeClose = reader.BuildIndexCallCount;

            await vm.CloseTabCommand.ExecuteAsync(originalTab);
            await vm.OpenFilePathAsync(filePath);

            var reopenedTab = Assert.Single(vm.Tabs);
            Assert.NotSame(originalTab, reopenedTab);
            Assert.Equal(FileEncoding.Utf16, reopenedTab.Encoding);
            Assert.Equal(FileEncoding.Utf16, reopenedTab.EffectiveEncoding);
            Assert.True(reopenedTab.IsPinned);
            Assert.False(reopenedTab.AutoScrollEnabled);
            Assert.True(reopenedTab.IsFilterActive);
            Assert.Equal(120, reopenedTab.FilteredLineCount);
            Assert.Equal(10, reopenedTab.ScrollPosition);
            Assert.Equal(11, reopenedTab.VisibleLines.First().LineNumber);
            Assert.Equal(42, reopenedTab.NavigateToLineNumber);
            Assert.Equal("Filter active: 120 matching lines.", reopenedTab.StatusText);
            Assert.Equal(buildCountBeforeClose, reader.BuildIndexCallCount);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task CloseTab_RecentReopen_DoesNotRestoreStateAcrossScopes()
    {
        var vm = CreateViewModel();
        try
        {
            await vm.InitializeAsync();

            const string filePath = @"C:\test\shared-scope.log";
            await vm.OpenFilePathAsync(filePath);
            await vm.CreateGroupCommand.ExecuteAsync(null);
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var adHocTab = Assert.Single(vm.Tabs);
            var dashboardA = vm.Groups[0];
            var dashboardB = vm.Groups[1];
            dashboardA.Model.FileIds.Add(adHocTab.FileId);
            dashboardB.Model.FileIds.Add(adHocTab.FileId);

            vm.ToggleGroupSelection(dashboardA);
            await vm.OpenFilePathAsync(filePath);
            var dashboardTabA = FindScopedTab(vm, filePath, dashboardA.Id);
            vm.TogglePinTab(dashboardTabA);
            await ChangeEncodingAndWaitForLoadAsync(dashboardTabA, FileEncoding.Utf16);
            await dashboardTabA.ApplyFilterAsync(
                Enumerable.Range(1, 20).ToArray(),
                "Filter active: 20 matching lines.",
                new SearchRequest
                {
                    Query = "Line",
                    FilePaths = new List<string> { filePath }
                },
                hasParseableTimestamps: false);

            await vm.CloseTabCommand.ExecuteAsync(dashboardTabA);

            vm.ToggleGroupSelection(dashboardB);
            await vm.OpenFilePathAsync(filePath);
            var dashboardTabB = FindScopedTab(vm, filePath, dashboardB.Id);

            Assert.Equal(FileEncoding.Auto, dashboardTabB.Encoding);
            Assert.Equal(FileEncoding.Utf8, dashboardTabB.EffectiveEncoding);
            Assert.False(dashboardTabB.IsPinned);
            Assert.False(dashboardTabB.IsFilterActive);
        }
        finally
        {
            vm.Dispose();
        }
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
        Assert.Contains(tab.TabInstanceId, openOrder.Keys);
        Assert.Contains(tab.TabInstanceId, pinOrder.Keys);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.DoesNotContain(tab.TabInstanceId, openOrder.Keys);
        Assert.DoesNotContain(tab.TabInstanceId, pinOrder.Keys);
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

        var host = CreateDashboardHost(vm);
        await host.OpenFilePathInScopeAsync(fileA.FilePath, dashboard.Id);
        await host.OpenFilePathInScopeAsync(fileB.FilePath, dashboard.Id);
        await WaitForConditionAsync(() =>
            dashboard.MemberFiles.Count == 3 &&
            dashboard.MemberFiles.Select(member => member.FileId).SequenceEqual(new[] { fileA.Id, fileB.Id, fileC.Id }) &&
            !dashboard.MemberFiles[0].HasError &&
            !dashboard.MemberFiles[1].HasError &&
            dashboard.MemberFiles[1].IsSelected &&
            dashboard.MemberFiles[2].HasError);

        await vm.CloseTabCommand.ExecuteAsync(FindScopedTab(vm, fileB.FilePath, dashboard.Id));
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
        Assert.Equal(
            new[] { @"C:\test\a.log", @"C:\test\b.log" },
            filtered.Select(tab => tab.FilePath).ToArray());
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
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        var filtered = vm.FilteredTabs.ToList();
        Assert.Single(filtered);
        Assert.Equal(@"C:\test\a.log", filtered[0].FilePath);
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
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        Assert.Equal("Scope: Payments (1)", vm.CurrentScopeSummaryText);

        vm.ShowAdHocTabsCommand.Execute(null);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Null(vm.ActiveDashboardId);
        Assert.All(vm.Groups, group => Assert.False(group.IsSelected));
        Assert.True(vm.IsAdHocScopeActive);
        Assert.Equal("Ad Hoc", vm.CurrentScopeLabel);
        Assert.Equal("Scope: Ad Hoc (2)", vm.CurrentScopeSummaryText);
        Assert.Equal("Ad Hoc (2)", vm.AdHocScopeChipText);
        Assert.Equal("2 of 3 tabs (Ad Hoc)", vm.TabCountText);
        Assert.Equal(
            new[] { @"C:\test\a.log", @"C:\test\b.log" },
            filtered.Select(tab => tab.FilePath).ToArray());
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
        Assert.Equal("Ad Hoc (1)", vm.AdHocScopeChipText);
        Assert.False(vm.IsCurrentScopeEmpty);
        Assert.Equal(groupIds, vm.Groups.Select(group => group.Id).ToArray());
    }

    [Fact]
    public async Task EmptyStateText_AdHocScopeWithoutUnassignedTabs_ExplainsWhyScopeIsEmpty()
    {
        var fileEntry = new LogFileEntry { FilePath = @"C:\test\a.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileEntry);
        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Name = "Payments",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { fileEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();
        var dashboard = Assert.Single(vm.Groups);
        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(fileEntry.FilePath);

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
        Assert.NotSame(tab, vm.SelectedTab);
        Assert.Equal(dashboard.Id, vm.SelectedTab!.ScopeDashboardId);
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Contains(vm.SelectedTab!, filtered);
        Assert.Single(filtered);
        Assert.Equal(tab.FilePath, filtered[0].FilePath);
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
        Assert.False(vm.IsCurrentScopeEmpty);

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

        vm.ToggleGroupSelection(dashboardB);
        await vm.OpenFilePathAsync(tabB.FilePath);
        var dashboardTabB = FindScopedTab(vm, tabB.FilePath, dashboardB.Id);

        vm.ToggleGroupSelection(dashboardA);
        Assert.Equal(dashboardA.Id, vm.ActiveDashboardId);
        Assert.DoesNotContain(dashboardTabB, vm.FilteredTabs);

        await vm.OpenFilePathAsync(tabB.FilePath);

        Assert.Equal(dashboardB.Id, vm.ActiveDashboardId);
        Assert.True(dashboardB.IsSelected);
        Assert.False(dashboardA.IsSelected);
        Assert.Same(dashboardTabB, vm.SelectedTab);
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
    public async Task OpenFilePathAsync_ModifierOwnedEffectivePath_ReusesDashboardScope()
    {
        Directory.CreateDirectory(_testRoot);
        var targetDate = DateTime.Today.AddDays(-1);
        var basePath = Path.Combine(_testRoot, "modifier-dashboard.log");
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

        var dashboard = Assert.Single(vm.Groups);
        await vm.ApplyDashboardModifierAsync(
            dashboard,
            daysBack: 1,
            new ReplacementPattern
            {
                Id = "pattern-1",
                FindPattern = ".log",
                ReplacePattern = ".log{yyyyMMdd}"
            });

        var existingScopedTab = FindScopedTab(vm, effectivePath, dashboard.Id);
        Assert.NotNull(existingScopedTab);
        await vm.CloseTabCommand.ExecuteAsync(existingScopedTab);
        vm.ShowAdHocTabsCommand.Execute(null);

        await vm.OpenFilePathAsync(effectivePath);

        var reopenedTab = FindScopedTab(vm, effectivePath, dashboard.Id);
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.True(dashboard.IsSelected);
        Assert.NotNull(reopenedTab);
        Assert.Same(reopenedTab, vm.SelectedTab);
        Assert.Contains(reopenedTab, vm.FilteredTabs);
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

        vm.ToggleGroupSelection(g2);
        await vm.OpenFilePathAsync(tabB.FilePath);
        var dashboardTabB = FindScopedTab(vm, tabB.FilePath, g2.Id);

        vm.ToggleGroupSelection(g1);
        Assert.Equal(g1.Id, vm.ActiveDashboardId);
        Assert.DoesNotContain(dashboardTabB, vm.FilteredTabs);

        await vm.NavigateToLineAsync(tabB.FilePath, 42);

        Assert.Equal(g2.Id, vm.ActiveDashboardId);
        Assert.True(g2.IsSelected);
        Assert.False(g1.IsSelected);
        Assert.Same(dashboardTabB, vm.SelectedTab);
        Assert.Single(vm.FilteredTabs);
        Assert.Contains(dashboardTabB, vm.FilteredTabs);
        Assert.Equal(42, dashboardTabB.NavigateToLineNumber);
    }

    [Fact]
    public async Task NavigateToLineAsync_AdHocEffectivePathWithoutActiveDashboard_ReusesAdHocScope()
    {
        Directory.CreateDirectory(_testRoot);
        var targetDate = DateTime.Today.AddDays(-1);
        var basePath = Path.Combine(_testRoot, "adhoc-navigation.log");
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

        var existingAdHocTab = vm.Tabs.Single(tab =>
            string.Equals(tab.FilePath, effectivePath, StringComparison.OrdinalIgnoreCase) &&
            tab.IsAdHocScope);
        await vm.CloseTabCommand.ExecuteAsync(existingAdHocTab);

        await vm.NavigateToLineAsync(effectivePath, 42);

        var reopenedAdHocTab = vm.Tabs.Single(tab =>
            string.Equals(tab.FilePath, effectivePath, StringComparison.OrdinalIgnoreCase) &&
            tab.IsAdHocScope);
        Assert.Null(vm.ActiveDashboardId);
        Assert.True(vm.IsAdHocScopeActive);
        Assert.Same(reopenedAdHocTab, vm.SelectedTab);
        Assert.Contains(reopenedAdHocTab, vm.FilteredTabs);
        Assert.Equal(42, reopenedAdHocTab.NavigateToLineNumber);
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
        Assert.Equal(2, vm.FilteredTabs.Count());
        Assert.Contains(tabB, vm.FilteredTabs);
        Assert.Equal(77, tabB.NavigateToLineNumber);
    }

    [Fact]
    public async Task OpenFilePathAsync_ModifierOwnedPath_WinsOverContainingDashboardFallback()
    {
        Directory.CreateDirectory(_testRoot);
        var targetDate = DateTime.Today.AddDays(-1);
        var basePath = Path.Combine(_testRoot, "modifier-fallback.log");
        var effectivePath = $"{basePath}{targetDate:yyyyMMdd}";
        await File.WriteAllTextAsync(basePath, "base");
        await File.WriteAllTextAsync(effectivePath, "effective");

        var baseEntry = new LogFileEntry { FilePath = basePath };
        var effectiveEntry = new LogFileEntry { FilePath = effectivePath };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(baseEntry);
        await fileRepo.AddAsync(effectiveEntry);

        var groupRepo = new StubLogGroupRepository();
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "modifier-dashboard",
            Name = "Modifier Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { baseEntry.Id }
        });
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "fallback-dashboard",
            Name = "Fallback Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { effectiveEntry.Id }
        });

        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        var modifierDashboard = vm.Groups.Single(group => group.Id == "modifier-dashboard");
        var fallbackDashboard = vm.Groups.Single(group => group.Id == "fallback-dashboard");
        await vm.ApplyDashboardModifierAsync(
            modifierDashboard,
            daysBack: 1,
            new ReplacementPattern
            {
                Id = "pattern-1",
                FindPattern = ".log",
                ReplacePattern = ".log{yyyyMMdd}"
            });

        var existingScopedTab = FindScopedTab(vm, effectivePath, modifierDashboard.Id);
        Assert.NotNull(existingScopedTab);
        await vm.CloseTabCommand.ExecuteAsync(existingScopedTab);
        vm.ShowAdHocTabsCommand.Execute(null);

        await vm.OpenFilePathAsync(effectivePath);

        var scopedTab = FindScopedTab(vm, effectivePath, modifierDashboard.Id);
        Assert.Equal(modifierDashboard.Id, vm.ActiveDashboardId);
        Assert.True(modifierDashboard.IsSelected);
        Assert.False(fallbackDashboard.IsSelected);
        Assert.NotNull(scopedTab);
        Assert.Same(scopedTab, vm.SelectedTab);
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
        Assert.Equal("Current tab filter cleared.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public void FilterPanel_ClearFilterLabel_TracksCurrentTarget()
    {
        using var vm = CreateViewModel();

        Assert.Equal("Clear Tab Filter", vm.FilterPanel.ClearFilterLabel);

        vm.FilterPanel.IsAllOpenTabsTarget = true;

        Assert.Equal("Clear Open Tabs Filter", vm.FilterPanel.ClearFilterLabel);
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
    public async Task FilterPanel_ScopeSwitch_RestoresPerScopeInputsAndOutput()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        RefreshDashboardMemberFiles(dashboard, (adHocTabB.FileId, adHocTabB.FilePath));

        search.NextResult = new SearchResult
        {
            FilePath = adHocTabB.FilePath,
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 12, LineText = "adhoc hit", MatchStart = 0, MatchLength = 5 }
            },
            HasParseableTimestamps = true
        };

        vm.SelectedTab = adHocTabB;
        vm.FilterPanel.Query = "adhoc-state";
        vm.FilterPanel.IsRegex = true;
        vm.FilterPanel.CaseSensitive = true;
        vm.FilterPanel.FromTimestamp = "2026-03-09 19:49:10";
        vm.FilterPanel.ToTimestamp = "2026-03-09 19:49:20";
        vm.FilterPanel.IsCurrentTabTarget = true;

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var dashboardTabB = FindScopedTab(vm, @"C:\test\b.log", dashboard.Id);

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = dashboardTabB.FilePath,
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 33, LineText = "dashboard hit", MatchStart = 0, MatchLength = 9 }
                },
                HasParseableTimestamps = true
            }
        };

        vm.FilterPanel.Query = "dashboard-state";
        vm.FilterPanel.IsRegex = false;
        vm.FilterPanel.CaseSensitive = false;
        vm.FilterPanel.FromTimestamp = string.Empty;
        vm.FilterPanel.ToTimestamp = string.Empty;
        vm.FilterPanel.IsAllOpenTabsTarget = true;

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        vm.ToggleGroupSelection(dashboard);

        Assert.Equal("adhoc-state", vm.FilterPanel.Query);
        Assert.True(vm.FilterPanel.IsRegex);
        Assert.True(vm.FilterPanel.CaseSensitive);
        Assert.True(vm.FilterPanel.IsCurrentTabTarget);
        Assert.Equal("2026-03-09 19:49:10", vm.FilterPanel.FromTimestamp);
        Assert.Equal("2026-03-09 19:49:20", vm.FilterPanel.ToTimestamp);

        vm.SelectedTab = adHocTabB;
        Assert.Equal(FilterCurrentTabClearedStatusText, vm.FilterPanel.StatusText);

        vm.ToggleGroupSelection(dashboard);

        Assert.Equal("dashboard-state", vm.FilterPanel.Query);
        Assert.False(vm.FilterPanel.IsRegex);
        Assert.False(vm.FilterPanel.CaseSensitive);
        Assert.True(vm.FilterPanel.IsAllOpenTabsTarget);
        Assert.Equal("Filter active across 1 open tab(s): 1 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_CurrentTab_SelectedTabChangesStayStaleAndClearOnlyAffectsSelectedTab()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var originalTab = vm.SelectedTab!;
        var otherTab = vm.Tabs.Single(tab => !ReferenceEquals(tab, originalTab));

        search.NextResult = new SearchResult
        {
            FilePath = originalTab.FilePath,
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 2, LineText = "Line 2", MatchStart = 0, MatchLength = 4 }
            },
            HasParseableTimestamps = true
        };

        vm.FilterPanel.Query = "Line";
        vm.FilterPanel.IsCurrentTabTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        vm.SelectedTab = otherTab;

        Assert.Equal(FilterCurrentTabClearedStatusText, vm.FilterPanel.StatusText);

        await vm.FilterPanel.ClearFilterCommand.ExecuteAsync(null);

        Assert.False(otherTab.IsFilterActive);
        Assert.True(originalTab.IsFilterActive);
        Assert.Equal(FilterCurrentTabClearedStatusText, vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_AppliesOnlyAcrossVisibleOpenTabs()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabA.FileId, adHocTabA.FilePath),
            (adHocTabB.FileId, adHocTabB.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        var dashboardTabA = FindScopedTab(vm, @"C:\test\a.log", dashboard.Id);

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = @"C:\test\a.log",
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 2, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                },
                HasParseableTimestamps = true
            }
        };

        vm.FilterPanel.Query = "scope";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(1, search.SearchFilesCallCount);
        Assert.Equal(new[] { @"C:\test\a.log" }, search.LastSearchFilesRequest!.FilePaths);
        Assert.True(dashboardTabA.IsFilterActive);
        Assert.Equal(1, dashboardTabA.FilteredLineCount);
        Assert.Equal("Filter active across 1 open tab(s): 1 matching lines.", vm.FilterPanel.StatusText);

        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var dashboardTabB = FindScopedTab(vm, @"C:\test\b.log", dashboard.Id);

        Assert.False(dashboardTabB.IsFilterActive);
        Assert.Equal(FilterAllOpenTabsStaleStatusText, vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_DashboardPinningDoesNotMarkOutputStale()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabB.FileId, adHocTabB.FilePath),
            (adHocTabA.FileId, adHocTabA.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var dashboardTabA = FindScopedTab(vm, @"C:\test\a.log", dashboard.Id);

        search.NextResults =
        [
            new SearchResult
            {
                FilePath = @"C:\test\a.log",
                Hits = [new SearchHit { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }],
                HasParseableTimestamps = true
            },
            new SearchResult
            {
                FilePath = @"C:\test\b.log",
                Hits = [new SearchHit { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }],
                HasParseableTimestamps = true
            }
        ];

        vm.FilterPanel.Query = "scope";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        var baseStatus = vm.FilterPanel.StatusText;

        vm.TogglePinTab(dashboardTabA);
        Assert.Equal(baseStatus, vm.FilterPanel.StatusText);
        Assert.True(dashboardTabA.IsFilterActive);
        Assert.Equal(new[] { @"C:\test\a.log", @"C:\test\b.log" }, vm.FilteredTabs.Select(tab => tab.FilePath).ToArray());

        vm.TogglePinTab(dashboardTabA);
        Assert.Equal(baseStatus, vm.FilterPanel.StatusText);
        Assert.True(dashboardTabA.IsFilterActive);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_DashboardReorderMarksOutputStale()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabA.FileId, adHocTabA.FilePath),
            (adHocTabB.FileId, adHocTabB.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        search.NextResults =
        [
            new SearchResult
            {
                FilePath = @"C:\test\a.log",
                Hits = [new SearchHit { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }],
                HasParseableTimestamps = true
            },
            new SearchResult
            {
                FilePath = @"C:\test\b.log",
                Hits = [new SearchHit { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }],
                HasParseableTimestamps = true
            }
        ];

        vm.FilterPanel.Query = "scope";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
        Assert.Equal("Filter active across 2 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

        await vm.ReorderDashboardFileAsync(dashboard, adHocTabB.FileId, adHocTabA.FileId, DropPlacement.Before);

        Assert.Equal(FilterAllOpenTabsStaleStatusText, vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_WhenTailAppendsDuringActiveTabRestore_FullyReloadsSelectedTab()
    {
        var reader = new BlockingAppendableViewportRefreshLogReader(new[]
        {
            "INFO startup",
            "ERROR first",
            "INFO heartbeat",
            "ERROR second"
        });
        var tailService = new StubFileTailService();
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(logReader: reader, tailService: tailService, searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabA.FileId, adHocTabA.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        var dashboardTabA = FindScopedTab(vm, @"C:\test\a.log", dashboard.Id);

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = @"C:\test\a.log",
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 2, LineText = "ERROR first", MatchStart = 0, MatchLength = 5 },
                    new() { LineNumber = 4, LineText = "ERROR second", MatchStart = 0, MatchLength = 5 }
                },
                HasParseableTimestamps = true
            }
        };

        vm.FilterPanel.Query = "ERROR";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        vm.FilterPanel.SourceMode = SearchDataMode.SnapshotAndTail;
        reader.BlockNextRead();
        var applyTask = vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        await reader.BlockedReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        reader.AppendLine("ERROR third");
        tailService.RaiseLinesAppended(dashboardTabA.FilePath);
        reader.ReleaseBlockedRead();

        await applyTask;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var visibleLineNumbers = dashboardTabA.VisibleLines.Select(line => line.LineNumber).ToArray();
            if (dashboardTabA.TotalLines == 5 &&
                dashboardTabA.FilteredLineCount == 3 &&
                visibleLineNumbers.SequenceEqual(new[] { 2, 4, 5 }))
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.True(dashboardTabA.IsFilterActive);
        Assert.Equal(5, dashboardTabA.TotalLines);
        Assert.Equal(3, dashboardTabA.FilteredLineCount);
        Assert.Equal(new[] { 2, 4, 5 }, dashboardTabA.VisibleLines.Select(line => line.LineNumber).ToArray());
        Assert.Equal(new[] { "ERROR first", "ERROR second", "ERROR third" }, dashboardTabA.VisibleLines.Select(line => line.Text).ToArray());
        Assert.Equal("Snapshot + tail filter active across 1 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_OpenCloseRestoresOutputWhenSetMatchesAgain()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabA.FileId, adHocTabA.FilePath),
            (adHocTabB.FileId, adHocTabB.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = @"C:\test\a.log",
                Hits = new List<SearchHit> { new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 } },
                HasParseableTimestamps = true
            },
            new SearchResult
            {
                FilePath = @"C:\test\b.log",
                Hits = new List<SearchHit> { new() { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 } },
                HasParseableTimestamps = true
            }
        };

        vm.FilterPanel.Query = "scope";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal("Filter active across 2 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

        await vm.CloseTabCommand.ExecuteAsync(FindScopedTab(vm, @"C:\test\b.log", dashboard.Id));
        Assert.Equal(FilterAllOpenTabsStaleStatusText, vm.FilterPanel.StatusText);

        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var reopenedTabB = FindScopedTab(vm, @"C:\test\b.log", dashboard.Id);
        Assert.True(reopenedTabB.IsFilterActive);
        Assert.Equal(1, reopenedTabB.FilteredLineCount);
        Assert.Equal("Filter active across 2 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_DeferredReplayRestoresSuppressedTabsWhenSetMatchesAgain()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        var adHocTabC = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\c.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        dashboard.Model.FileIds.Add(adHocTabC.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabA.FileId, adHocTabA.FilePath),
            (adHocTabB.FileId, adHocTabB.FilePath),
            (adHocTabC.FileId, adHocTabC.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        search.NextResults =
        [
            new SearchResult
            {
                FilePath = @"C:\test\a.log",
                Hits = [new SearchHit { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }],
                HasParseableTimestamps = true
            },
            new SearchResult
            {
                FilePath = @"C:\test\b.log",
                Hits = [new SearchHit { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }],
                HasParseableTimestamps = true
            },
            new SearchResult
            {
                FilePath = @"C:\test\c.log",
                Hits = [new SearchHit { LineNumber = 3, LineText = "C hit", MatchStart = 0, MatchLength = 1 }],
                HasParseableTimestamps = true
            }
        ];

        vm.FilterPanel.Query = "scope";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        await vm.CloseTabCommand.ExecuteAsync(FindScopedTab(vm, @"C:\test\c.log", dashboard.Id));
        Assert.Equal(FilterAllOpenTabsStaleStatusText, vm.FilterPanel.StatusText);

        await vm.CloseTabCommand.ExecuteAsync(FindScopedTab(vm, @"C:\test\b.log", dashboard.Id));
        Assert.Equal(FilterAllOpenTabsStaleStatusText, vm.FilterPanel.StatusText);

        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var reopenedTabB = FindScopedTab(vm, @"C:\test\b.log", dashboard.Id);
        Assert.False(reopenedTabB.IsFilterActive);
        Assert.Equal(FilterAllOpenTabsStaleStatusText, vm.FilterPanel.StatusText);

        await vm.OpenFilePathAsync(@"C:\test\c.log");
        var reopenedTabC = FindScopedTab(vm, @"C:\test\c.log", dashboard.Id);
        Assert.True(reopenedTabB.IsFilterActive);
        Assert.True(reopenedTabC.IsFilterActive);
        Assert.Equal(1, reopenedTabB.FilteredLineCount);
        Assert.Equal(1, reopenedTabC.FilteredLineCount);
        Assert.Equal("Filter active across 3 open tab(s): 3 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_AdHocCloseAllTabsMarksOutputStale()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = @"C:\test\a.log",
                Hits = new List<SearchHit> { new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 } },
                HasParseableTimestamps = true
            },
            new SearchResult
            {
                FilePath = @"C:\test\b.log",
                Hits = new List<SearchHit> { new() { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 } },
                HasParseableTimestamps = true
            }
        };

        vm.FilterPanel.Query = "adhoc";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal("Filter active across 2 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

        await vm.CloseAllTabsAsync();

        Assert.Empty(vm.Tabs);
        Assert.Equal(FilterAllOpenTabsStaleStatusText, vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_ReopenAfterHiddenTabPurge_RestoresSnapshotsWithoutStaleStatus()
    {
        var fileRepo = new StubLogFileRepository();
        var search = new RecordingSearchService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmFilterPurge_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            var fileCPath = Path.Combine(testDir, "c.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");
            await File.WriteAllTextAsync(fileCPath, "c");

            var fileA = new LogFileEntry { FilePath = fileAPath };
            var fileB = new LogFileEntry { FilePath = fileBPath };
            var fileC = new LogFileEntry { FilePath = fileCPath };
            await fileRepo.AddAsync(fileA);
            await fileRepo.AddAsync(fileB);
            await fileRepo.AddAsync(fileC);

            var vm = CreateViewModel(fileRepo: fileRepo, searchService: search);
            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboardA = vm.Groups[0];
            var dashboardB = vm.Groups[1];
            dashboardA.Model.FileIds.Add(fileA.Id);
            dashboardA.Model.FileIds.Add(fileB.Id);
            dashboardB.Model.FileIds.Add(fileC.Id);
            RefreshDashboardMemberFiles(dashboardA, (fileA.Id, fileAPath), (fileB.Id, fileBPath));
            RefreshDashboardMemberFiles(dashboardB, (fileC.Id, fileCPath));

            vm.ToggleGroupSelection(dashboardA);
            await vm.OpenGroupFilesAsync(dashboardA);

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Hits = [new SearchHit { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                }
            ];

            vm.FilterPanel.Query = "scope";
            vm.FilterPanel.IsAllOpenTabsTarget = true;
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

            Assert.Equal("Filter active across 2 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

            vm.ToggleGroupSelection(dashboardB);
            await vm.OpenGroupFilesAsync(dashboardB);
            vm.HiddenTabPurgeAfter = TimeSpan.Zero;
            vm.RunTabLifecycleMaintenance();

            Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.ScopeDashboardId, dashboardA.Id, StringComparison.Ordinal));

            var reopenTask = vm.HandleDashboardGroupInvokedAsync(dashboardA);
            Assert.Equal("Filter active across 2 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);
            await reopenTask;

            var reopenedTabA = FindScopedTab(vm, fileAPath, dashboardA.Id);
            var reopenedTabB = FindScopedTab(vm, fileBPath, dashboardA.Id);
            Assert.True(reopenedTabA.IsFilterActive);
            Assert.True(reopenedTabB.IsFilterActive);
            Assert.Equal(1, reopenedTabA.FilteredLineCount);
            Assert.Equal(1, reopenedTabB.FilteredLineCount);
            Assert.Equal("Filter active across 2 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_ReopenDuringDashboardLoad_RestoresEarlyTabsWithoutLatchingStale()
    {
        var fileRepo = new StubLogFileRepository();
        var search = new RecordingSearchService();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmFilterReopen_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fastPath = Path.Combine(testDir, "fast.log");
            var slowPath = Path.Combine(testDir, "slow.log");
            var otherPath = Path.Combine(testDir, "other.log");
            await File.WriteAllTextAsync(fastPath, "fast");
            await File.WriteAllTextAsync(slowPath, "slow");
            await File.WriteAllTextAsync(otherPath, "other");

            var fastEntry = new LogFileEntry { FilePath = fastPath };
            var slowEntry = new LogFileEntry { FilePath = slowPath };
            var otherEntry = new LogFileEntry { FilePath = otherPath };
            await fileRepo.AddAsync(fastEntry);
            await fileRepo.AddAsync(slowEntry);
            await fileRepo.AddAsync(otherEntry);

            var logReader = new ArmableBlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                searchService: search,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboardA = vm.Groups[0];
            var dashboardB = vm.Groups[1];
            dashboardA.Model.FileIds.Add(fastEntry.Id);
            dashboardA.Model.FileIds.Add(slowEntry.Id);
            dashboardB.Model.FileIds.Add(otherEntry.Id);
            RefreshDashboardMemberFiles(dashboardA, (fastEntry.Id, fastPath), (slowEntry.Id, slowPath));
            RefreshDashboardMemberFiles(dashboardB, (otherEntry.Id, otherPath));

            vm.ToggleGroupSelection(dashboardA);
            await vm.OpenGroupFilesAsync(dashboardA);

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fastPath,
                    Hits = [new SearchHit { LineNumber = 1, LineText = "fast hit", MatchStart = 0, MatchLength = 4 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = slowPath,
                    Hits = [new SearchHit { LineNumber = 2, LineText = "slow hit", MatchStart = 0, MatchLength = 4 }],
                    HasParseableTimestamps = true
                }
            ];

            vm.FilterPanel.Query = "scope";
            vm.FilterPanel.IsAllOpenTabsTarget = true;
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

            vm.ToggleGroupSelection(dashboardB);
            await vm.OpenGroupFilesAsync(dashboardB);
            vm.HiddenTabPurgeAfter = TimeSpan.Zero;
            vm.RunTabLifecycleMaintenance();

            Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.ScopeDashboardId, dashboardA.Id, StringComparison.Ordinal));

            logReader.ArmBlocking();
            var loadTask = vm.HandleDashboardGroupInvokedAsync(dashboardA);
            await logReader.WaitForBlockedBuildAsync();

            await WaitForConditionAsync(() =>
            {
                var reopenedFastTab = vm.Tabs.FirstOrDefault(tab =>
                    string.Equals(tab.ScopeDashboardId, dashboardA.Id, StringComparison.Ordinal) &&
                    string.Equals(tab.FilePath, fastPath, StringComparison.OrdinalIgnoreCase));
                return reopenedFastTab is { IsFilterActive: true };
            });

            var earlyFastTab = FindScopedTab(vm, fastPath, dashboardA.Id);
            Assert.True(vm.IsDashboardLoading);
            Assert.True(earlyFastTab.IsFilterActive);
            Assert.Equal(1, earlyFastTab.FilteredLineCount);
            Assert.NotEqual(FilterAllOpenTabsStaleStatusText, vm.FilterPanel.StatusText);

            logReader.ReleaseBlockedBuild();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var reopenedSlowTab = FindScopedTab(vm, slowPath, dashboardA.Id);
            Assert.False(vm.IsDashboardLoading);
            Assert.True(earlyFastTab.IsFilterActive);
            Assert.True(reopenedSlowTab.IsFilterActive);
            Assert.Equal(1, reopenedSlowTab.FilteredLineCount);
            Assert.Equal("Filter active across 2 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task FilterPanel_CurrentTab_SourceModeChangeAfterApply_ShowsStaleStatus()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");

        search.SearchFileAsyncHandler = (_, request, _, _) => Task.FromResult(new SearchResult
        {
            FilePath = request.FilePaths.Single(),
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 2, LineText = "Line 2", MatchStart = 0, MatchLength = 4 },
                new() { LineNumber = 5, LineText = "Line 5", MatchStart = 0, MatchLength = 4 }
            },
            HasParseableTimestamps = true
        });

        vm.FilterPanel.Query = "Line";
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
        Assert.Equal("Filter active: 2 matching lines.", vm.FilterPanel.StatusText);

        vm.SearchPanel.SearchDataMode = SearchDataMode.Tail;

        Assert.NotEqual("Filter active across 1 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_SourceModeRoundTrip_RestoresActiveStatusAndSearchUsesSnapshots()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");

        search.SearchFilesAsyncHandler = (request, _, _) => Task.FromResult<IReadOnlyList<SearchResult>>(
        [
            new SearchResult
            {
                FilePath = @"C:\test\filtered.log",
                Hits =
                [
                    new SearchHit { LineNumber = 2, LineText = "Line 2", MatchStart = 0, MatchLength = 4 },
                    new SearchHit { LineNumber = 5, LineText = "Line 5", MatchStart = 0, MatchLength = 4 }
                ],
                HasParseableTimestamps = true
            }
        ]);

        vm.FilterPanel.Query = "Line";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal("Filter active across 1 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

        vm.SearchPanel.SearchDataMode = SearchDataMode.Tail;

        Assert.NotEqual("Filter active across 1 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

        vm.SearchPanel.SearchDataMode = SearchDataMode.DiskSnapshot;

        Assert.Equal("Filter active across 1 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

        search.NextResults = Array.Empty<SearchResult>();
        vm.SearchPanel.Query = "search";
        await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 2, 5 }, search.LastSearchFilesRequest!.AllowedLineNumbersByFilePath[@"C:\test\filtered.log"]);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_TargetModeRoundTrip_RestoresActiveStatus()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");

        search.SearchFilesAsyncHandler = (request, _, _) => Task.FromResult<IReadOnlyList<SearchResult>>(
        [
            new SearchResult
            {
                FilePath = @"C:\test\filtered.log",
                Hits =
                [
                    new SearchHit { LineNumber = 2, LineText = "Line 2", MatchStart = 0, MatchLength = 4 },
                    new SearchHit { LineNumber = 5, LineText = "Line 5", MatchStart = 0, MatchLength = 4 }
                ],
                HasParseableTimestamps = true
            }
        ]);

        vm.FilterPanel.Query = "Line";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal("Filter active across 1 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

        vm.FilterPanel.IsCurrentTabTarget = true;

        Assert.NotEqual("Filter active across 1 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);

        vm.FilterPanel.IsAllOpenTabsTarget = true;

        Assert.Equal("Filter active across 1 open tab(s): 2 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task SearchPanel_CurrentFile_UsesActiveFilterLineSubset()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");

        search.SearchFileAsyncHandler = (_, request, _, _) => Task.FromResult(new SearchResult
        {
            FilePath = request.FilePaths.Single(),
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 2, LineText = "Line 2", MatchStart = 0, MatchLength = 4 },
                new() { LineNumber = 5, LineText = "Line 5", MatchStart = 0, MatchLength = 4 }
            },
            HasParseableTimestamps = true
        });
        search.SearchFilesAsyncHandler = (request, _, _) => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        vm.FilterPanel.Query = "Line";
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        vm.SearchPanel.Query = "search";
        await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastSearchFilesRequest);
        Assert.True(search.LastSearchFilesRequest!.AllowedLineNumbersByFilePath.TryGetValue(vm.SelectedTab!.FilePath, out var allowedLines));
        Assert.Equal(new[] { 2, 5 }, allowedLines);
    }

    [Fact]
    public async Task SearchPanel_AllOpenTabs_UsesVisibleOpenTabsOnly()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabA.FileId, adHocTabA.FilePath),
            (adHocTabB.FileId, adHocTabB.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        vm.SearchPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
        vm.SearchPanel.Query = "scope-search";
        await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastSearchFilesRequest);
        Assert.Equal(new[] { @"C:\test\a.log" }, search.LastSearchFilesRequest!.FilePaths);
    }

    [Fact]
    public async Task SearchPanel_AllOpenTabs_TailMode_DoesNotMaterializeUnopenedMembersInDashboardScope()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabA.FileId, adHocTabA.FilePath),
            (adHocTabB.FileId, adHocTabB.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        vm.SearchPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
        vm.SearchPanel.Query = "scope-search";
        vm.SearchPanel.IsTailMode = true;
        await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.Tabs, tab =>
            string.Equals(tab.FilePath, @"C:\test\b.log", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal));
        Assert.Single(vm.Tabs.Where(tab =>
            string.Equals(tab.FilePath, @"C:\test\b.log", StringComparison.OrdinalIgnoreCase) &&
            tab.IsAdHocScope));
        Assert.Single(vm.Tabs.Where(tab => string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal)));

        vm.SearchPanel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public void SearchAndFilterPanels_KeepTargetModeSynchronized()
    {
        using var vm = CreateViewModel();

        Assert.Equal(SearchFilterTargetMode.CurrentTab, vm.SearchPanel.TargetMode);
        Assert.Equal(SearchFilterTargetMode.CurrentTab, vm.FilterPanel.TargetMode);

        vm.SearchPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
        Assert.Equal(SearchFilterTargetMode.AllOpenTabs, vm.FilterPanel.TargetMode);

        vm.FilterPanel.TargetMode = SearchFilterTargetMode.CurrentTab;
        Assert.Equal(SearchFilterTargetMode.CurrentTab, vm.SearchPanel.TargetMode);
    }

    [Fact]
    public void SearchAndFilterPanels_KeepSourceModeSynchronized()
    {
        using var vm = CreateViewModel();

        Assert.Equal(SearchDataMode.DiskSnapshot, vm.SearchPanel.SearchDataMode);
        Assert.Equal(SearchDataMode.DiskSnapshot, vm.FilterPanel.SourceMode);

        vm.SearchPanel.SearchDataMode = SearchDataMode.Tail;
        Assert.Equal(SearchDataMode.Tail, vm.FilterPanel.SourceMode);

        vm.FilterPanel.SourceMode = SearchDataMode.SnapshotAndTail;
        Assert.Equal(SearchDataMode.SnapshotAndTail, vm.SearchPanel.SearchDataMode);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_WarningsAndClearHandleDeferredState()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log" && tab.IsAdHocScope);
        var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log" && tab.IsAdHocScope);
        var adHocTabC = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\c.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabA.FileId);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);
        dashboard.Model.FileIds.Add(adHocTabC.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocTabA.FileId, adHocTabA.FilePath),
            (adHocTabB.FileId, adHocTabB.FilePath),
            (adHocTabC.FileId, adHocTabC.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");
        var dashboardTabA = FindScopedTab(vm, @"C:\test\a.log", dashboard.Id);
        var dashboardTabC = FindScopedTab(vm, @"C:\test\c.log", dashboard.Id);

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = @"C:\test\a.log",
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 3, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                },
                HasParseableTimestamps = true
            },
            new SearchResult
            {
                FilePath = @"C:\test\b.log",
                Error = "boom"
            },
            new SearchResult
            {
                FilePath = @"C:\test\c.log",
                Hits = new List<SearchHit>(),
                HasParseableTimestamps = false
            }
        };

        vm.FilterPanel.Query = "scope";
        vm.FilterPanel.FromTimestamp = "2026-03-09 19:49:10";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.True(dashboardTabA.IsFilterActive);
        Assert.Equal("Filter active across 2 open tab(s): 1 matching lines. 2 warning(s).", vm.FilterPanel.StatusText);
        Assert.Equal(2, vm.FilterPanel.Warnings.Count);
        Assert.Contains(vm.FilterPanel.Warnings, warning => warning.FilePath == @"C:\test\b.log" && warning.Message.Contains("boom"));
        Assert.Contains(vm.FilterPanel.Warnings, warning => warning.FilePath == @"C:\test\c.log" && warning.Message.Contains("No parseable timestamps"));

        await vm.FilterPanel.ClearFilterCommand.ExecuteAsync(null);

        Assert.False(dashboardTabA.IsFilterActive);
        Assert.False(dashboardTabC.IsFilterActive);
        Assert.Equal("All open tabs filter cleared.", vm.FilterPanel.StatusText);
        Assert.Empty(vm.FilterPanel.Warnings);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_SnapshotAndTail_WithNoOpenTabs_DoesNotMaterializeScopedTab()
    {
        var reader = new BlockingAppendableViewportRefreshLogReader(new[]
        {
            "INFO startup",
            "ERROR first"
        });
        var tailService = new StubFileTailService();
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(logReader: reader, tailService: tailService, searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\shared.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboardA = vm.Groups[0];
        var dashboardB = vm.Groups[1];
        var adHocTab = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\shared.log" && tab.IsAdHocScope);
        dashboardA.Model.FileIds.Add(adHocTab.FileId);
        dashboardB.Model.FileIds.Add(adHocTab.FileId);
        RefreshDashboardMemberFiles(
            dashboardA,
            (adHocTab.FileId, adHocTab.FilePath));
        RefreshDashboardMemberFiles(
            dashboardB,
            (adHocTab.FileId, adHocTab.FilePath));

        vm.ToggleGroupSelection(dashboardB);
        await vm.OpenFilePathAsync(@"C:\test\shared.log");
        var dashboardTabB = FindScopedTab(vm, @"C:\test\shared.log", dashboardB.Id);

        vm.ToggleGroupSelection(dashboardA);

        vm.FilterPanel.Query = "ERROR";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        vm.FilterPanel.SourceMode = SearchDataMode.SnapshotAndTail;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(0, search.SearchFilesCallCount);
        Assert.Equal("No open tabs to filter.", vm.FilterPanel.StatusText);
        Assert.False(dashboardTabB.IsFilterActive);
        Assert.DoesNotContain(vm.Tabs, tab =>
            string.Equals(tab.FilePath, @"C:\test\shared.log", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tab.ScopeDashboardId, dashboardA.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_DiskSnapshot_AppliesOnlyToVisibleOpenTabsInActiveScope()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\shared.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboardA = vm.Groups[0];
        var dashboardB = vm.Groups[1];
        var adHocTab = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\shared.log" && tab.IsAdHocScope);
        dashboardA.Model.FileIds.Add(adHocTab.FileId);
        dashboardB.Model.FileIds.Add(adHocTab.FileId);
        RefreshDashboardMemberFiles(
            dashboardA,
            (adHocTab.FileId, adHocTab.FilePath));
        RefreshDashboardMemberFiles(
            dashboardB,
            (adHocTab.FileId, adHocTab.FilePath));

        vm.ToggleGroupSelection(dashboardB);
        await vm.OpenFilePathAsync(@"C:\test\shared.log");
        var dashboardTabB = FindScopedTab(vm, @"C:\test\shared.log", dashboardB.Id);

        vm.ToggleGroupSelection(dashboardA);
        await vm.OpenFilePathAsync(@"C:\test\shared.log");

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = @"C:\test\shared.log",
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 2, LineText = "ERROR first", MatchStart = 0, MatchLength = 5 }
                },
                HasParseableTimestamps = true
            }
        };

        vm.FilterPanel.Query = "ERROR";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        vm.FilterPanel.SourceMode = SearchDataMode.DiskSnapshot;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        var dashboardTabA = FindScopedTab(vm, @"C:\test\shared.log", dashboardA.Id);
        Assert.Equal(1, search.SearchFilesCallCount);
        Assert.Equal(new[] { @"C:\test\shared.log" }, search.LastSearchFilesRequest!.FilePaths);
        Assert.True(dashboardTabA.IsFilterActive);
        Assert.Equal(1, dashboardTabA.FilteredLineCount);
        Assert.False(dashboardTabB.IsFilterActive);
    }

    [Fact]
    public async Task FilterPanel_AllOpenTabs_UsesVisibleOpenTabEncodings()
    {
        var utf8Path = Path.Combine(_testRoot, "encoding-a.log");
        var utf16Path = Path.Combine(_testRoot, "encoding-b.log");
        Directory.CreateDirectory(_testRoot);
        await File.WriteAllTextAsync(utf8Path, "plain utf8\n");
        await File.WriteAllTextAsync(utf16Path, "utf16 text\n", Encoding.Unicode);

        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(utf8Path);
        await vm.OpenFilePathAsync(utf16Path);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        var adHocUtf8 = vm.Tabs.Single(tab => string.Equals(tab.FilePath, utf8Path, StringComparison.OrdinalIgnoreCase) && tab.IsAdHocScope);
        var adHocUtf16 = vm.Tabs.Single(tab => string.Equals(tab.FilePath, utf16Path, StringComparison.OrdinalIgnoreCase) && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocUtf8.FileId);
        dashboard.Model.FileIds.Add(adHocUtf16.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (adHocUtf8.FileId, adHocUtf8.FilePath),
            (adHocUtf16.FileId, adHocUtf16.FilePath));

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(utf8Path);
        await vm.OpenFilePathAsync(utf16Path);

        search.NextResults = new[]
        {
            new SearchResult { FilePath = utf8Path, HasParseableTimestamps = true },
            new SearchResult { FilePath = utf16Path, HasParseableTimestamps = true }
        };

        vm.FilterPanel.Query = "encoding";
        vm.FilterPanel.IsAllOpenTabsTarget = true;
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastSearchFilesEncodings);
        Assert.Equal(FileEncoding.Utf8, search.LastSearchFilesEncodings![utf8Path]);
        Assert.Equal(FileEncoding.Utf16, search.LastSearchFilesEncodings![utf16Path]);
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
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.True(dashboard.IsSelected);

        await vm.CloseAllTabsAsync();

        Assert.Equal(2, vm.Tabs.Count);
        Assert.All(vm.Tabs, tab => Assert.True(tab.IsAdHocScope));
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
    public async Task AdHocMemberTabs_RemainAvailableWhileDashboardScopeIsActive()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        Assert.False(vm.IsAdHocScopeActive);
        Assert.True(vm.CanExpandAdHoc);
        Assert.Equal(
            new[] { @"C:\test\a.log", @"C:\test\b.log" },
            vm.AdHocMemberTabs.Select(tab => tab.FilePath).ToArray());
    }

    [Fact]
    public async Task AdHocExpansion_CanRemainExpandedWhileDashboardScopeIsActive()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        vm.IsAdHocExpanded = true;
        vm.ToggleGroupSelection(vm.Groups[0]);

        Assert.True(vm.IsAdHocExpanded);
    }

    [Fact]
    public async Task OpenAdHocMemberFile_ActivatesAdHocScopeAndSelectsTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        var adHocTab = vm.AdHocMemberTabs.Single(tab => tab.FilePath == @"C:\test\b.log");

        vm.OpenAdHocMemberFile(adHocTab);

        Assert.True(vm.IsAdHocScopeActive);
        Assert.Null(vm.ActiveDashboardId);
        Assert.Same(adHocTab, vm.SelectedTab);
    }

    [Fact]
    public async Task ClearAdHocTabs_ClosesOnlyAdHocTabsAndLeavesDashboardTabsOpen()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        var dashboardTab = vm.Tabs.Single(tab => tab.ScopeDashboardId == dashboard.Id);

        await vm.ClearAdHocTabsAsync();

        Assert.Single(vm.Tabs);
        Assert.Same(dashboardTab, vm.Tabs[0]);
        Assert.False(vm.Tabs[0].IsAdHocScope);
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.False(vm.CanExpandAdHoc);
    }

    [Fact]
    public async Task ClearAdHocTabs_WhenEmpty_IsNoOp()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.ClearAdHocTabsAsync();

        var remainingTab = Assert.Single(vm.Tabs);

        await vm.ClearAdHocTabsAsync();

        Assert.Same(remainingTab, Assert.Single(vm.Tabs));
        Assert.False(vm.CanExpandAdHoc);
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
    public async Task GetFilteredTabsSnapshot_ReplacesCachedSnapshotWhenScopeChanges()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var initialSnapshot = vm.GetFilteredTabsSnapshot();
        Assert.Same(initialSnapshot, vm.FilteredTabs);

        vm.TogglePinTab(vm.Tabs[1]);

        var pinnedSnapshot = vm.GetFilteredTabsSnapshot();
        Assert.NotSame(initialSnapshot, pinnedSnapshot);
        Assert.Equal(new[] { @"C:\test\b.log", @"C:\test\a.log" }, pinnedSnapshot.Select(tab => tab.FilePath).ToArray());

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        var dashboardSnapshot = vm.GetFilteredTabsSnapshot();
        Assert.NotSame(pinnedSnapshot, dashboardSnapshot);
        Assert.Equal(new[] { @"C:\test\a.log" }, dashboardSnapshot.Select(tab => tab.FilePath).ToArray());
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
    public async Task NavigateToLineAsync_SuppressedDuringDashboardLoad_DoesNothing()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tabA = vm.Tabs.First(tab => tab.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(tab => tab.FilePath == @"C:\test\b.log");
        vm.SelectedTab = tabA;
        vm.IsDashboardLoading = true;

        await vm.NavigateToLineAsync(
            tabB.FilePath,
            42,
            disableAutoScroll: true,
            suppressDuringDashboardLoad: true);

        Assert.True(vm.GlobalAutoScrollEnabled);
        Assert.Same(tabA, vm.SelectedTab);
        Assert.Equal(-1, tabB.NavigateToLineNumber);
    }

    [Fact]
    public async Task NavigateToLineAsync_SuppressedDuringDashboardLoad_DuringScopeResolution_DoesNotOpenOrNavigate()
    {
        Directory.CreateDirectory(_testRoot);
        var sourcePath = Path.Combine(_testRoot, "source.log");
        var targetPath = Path.Combine(_testRoot, "target.log");
        await File.WriteAllTextAsync(sourcePath, "source");
        await File.WriteAllTextAsync(targetPath, "target");

        var fileRepo = new ArmableBlockingLogFileRepository();
        await fileRepo.AddAsync(new LogFileEntry { FilePath = targetPath });

        var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(sourcePath);

        var sourceTab = Assert.Single(vm.Tabs);
        vm.SelectedTab = sourceTab;
        fileRepo.ArmGetByPathsBlocking();

        var navigationTask = vm.NavigateToLineAsync(
            targetPath,
            42,
            disableAutoScroll: true,
            suppressDuringDashboardLoad: true);

        await fileRepo.WaitForBlockedGetByPathsAsync();

        vm.IsDashboardLoading = true;
        fileRepo.ReleaseBlockedGetByPaths();
        await navigationTask;

        Assert.True(vm.GlobalAutoScrollEnabled);
        Assert.Same(sourceTab, vm.SelectedTab);
        Assert.Single(vm.Tabs);
        Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-1, sourceTab.NavigateToLineNumber);
    }

    [Fact]
    public async Task NavigateToLineAsync_SuppressedDuringDashboardLoad_DuringFileOpen_DoesNotCommitTabOrNavigate()
    {
        Directory.CreateDirectory(_testRoot);
        var targetPath = Path.Combine(_testRoot, "blocked-target.log");
        var sourcePath = Path.Combine(_testRoot, "existing.log");
        await File.WriteAllTextAsync(targetPath, "target");
        await File.WriteAllTextAsync(sourcePath, "source");

        var fileRepo = new StubLogFileRepository();
        var logReader = new ReleasableBlockingLogReaderService(targetPath);
        await fileRepo.AddAsync(new LogFileEntry { FilePath = targetPath });

        var vm = CreateViewModel(fileRepo: fileRepo, logReader: logReader);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(sourcePath);

        var sourceTab = Assert.Single(vm.Tabs);
        vm.SelectedTab = sourceTab;

        var navigationTask = vm.NavigateToLineAsync(
            targetPath,
            42,
            disableAutoScroll: true,
            suppressDuringDashboardLoad: true);

        await logReader.WaitForBlockedBuildAsync();

        vm.IsDashboardLoading = true;
        logReader.ReleaseBlockedBuild();
        await navigationTask;

        Assert.True(vm.GlobalAutoScrollEnabled);
        Assert.Same(sourceTab, vm.SelectedTab);
        Assert.Single(vm.Tabs);
        Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-1, sourceTab.NavigateToLineNumber);
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

        vm.ToggleGroupSelection(group);
        await vm.OpenGroupFilesAsync(group);

        Assert.Empty(vm.Tabs);
        Assert.True(vm.IsAdHocScopeActive);
        Assert.Null(vm.ActiveDashboardId);
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
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

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
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        await vm.MoveDashboardFileAsync(source, target, vm.Tabs[0].FileId, vm.Tabs[2].FileId, DropPlacement.Before);
        await vm.OpenFilePathAsync(@"C:\test\a.log");

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
    public async Task GetSearchResultFileOrderSnapshot_DashboardScope_PinnedTabs_DoNotChangeDashboardMemberOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        dashboard.Model.FileIds.Add(vm.Tabs[1].FileId);
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var scopedTabA = vm.Tabs.First(tab =>
            string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal) &&
            string.Equals(tab.FilePath, @"C:\test\a.log", StringComparison.OrdinalIgnoreCase));
        vm.TogglePinTab(scopedTabA);

        Assert.Equal(
            new[] { @"C:\test\b.log", @"C:\test\a.log" },
            vm.GetSearchResultFileOrderSnapshot().ToArray());
    }

    [Fact]
    public async Task GetSearchResultFileOrderSnapshot_ModifierDashboard_UsesResolvedMemberFilesOrder()
    {
        var fileRepo = new StubLogFileRepository();
        Directory.CreateDirectory(_testRoot);
        var basePathA = Path.Combine(_testRoot, "order-a.log");
        var basePathB = Path.Combine(_testRoot, "order-b.log");
        var targetDate = DateTime.Today.AddDays(-1);
        var effectivePathA = $"{basePathA}.{targetDate:yyyyMMdd}";
        var effectivePathB = $"{basePathB}.{targetDate:yyyyMMdd}";
        await File.WriteAllTextAsync(basePathA, "base-a");
        await File.WriteAllTextAsync(basePathB, "base-b");
        await File.WriteAllTextAsync(effectivePathA, "effective-a");
        await File.WriteAllTextAsync(effectivePathB, "effective-b");

        var fileEntryA = new LogFileEntry { FilePath = basePathA };
        var fileEntryB = new LogFileEntry { FilePath = basePathB };
        await fileRepo.AddAsync(fileEntryA);
        await fileRepo.AddAsync(fileEntryB);

        using var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        dashboard.Model.FileIds.Add(fileEntryB.Id);
        dashboard.Model.FileIds.Add(fileEntryA.Id);

        await vm.ApplyDashboardModifierAsync(
            dashboard,
            daysBack: 1,
            new ReplacementPattern
            {
                Id = "pattern-1",
                FindPattern = ".log",
                ReplacePattern = ".log.{yyyyMMdd}"
            });

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenFilePathAsync(effectivePathA);
        await vm.OpenFilePathAsync(effectivePathB);
        var scopedTabA = vm.Tabs.First(tab =>
            string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal) &&
            string.Equals(tab.FilePath, effectivePathA, StringComparison.OrdinalIgnoreCase));
        vm.TogglePinTab(scopedTabA);

        Assert.Equal(
            new[] { effectivePathB, effectivePathA },
            dashboard.MemberFiles.Select(member => member.FilePath).ToArray());
        Assert.Equal(
            new[] { effectivePathB, effectivePathA },
            vm.GetSearchResultFileOrderSnapshot().ToArray());
    }

    [Fact]
    public async Task OpenGroupFilesAsync_SelectingAnotherDashboard_DuringDashboardLoad_IsIgnored()
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

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
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
            await vm.OpenGroupFilesAsync(fastDashboard);

            Assert.False(logReader.BlockedBuildCanceled);
            Assert.Equal(slowDashboard.Id, vm.ActiveDashboardId);
            Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.FilePath, fastPath, StringComparison.OrdinalIgnoreCase));

            logReader.ReleaseBlockedBuild();
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(logReader.BlockedBuildCanceled);
            Assert.Equal(slowDashboard.Id, vm.ActiveDashboardId);
            Assert.Single(vm.Tabs);
            Assert.Equal(slowPath, vm.Tabs[0].FilePath);
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenFilePathAsync_AndAdHocSwitch_DuringDashboardLoad_AreIgnored()
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

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
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

            Assert.Null(vm.SelectedTab);
            Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.FilePath, fastPath, StringComparison.OrdinalIgnoreCase));
            Assert.True(vm.ShouldShowEmptyState);

            vm.ShowAdHocTabsCommand.Execute(null);

            Assert.Equal(dashboard.Id, vm.ActiveDashboardId);

            logReader.ReleaseBlockedBuild();
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(vm.IsDashboardLoading);
            Assert.Equal(2, vm.Tabs.Count);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenGroupFilesAsync_ParallelDashboardLoad_PreservesDashboardOrder()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmParallelOrder_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var orderedPaths = new[]
            {
                Path.Combine(testDir, "d.log"),
                Path.Combine(testDir, "b.log"),
                Path.Combine(testDir, "a.log"),
                Path.Combine(testDir, "c.log")
            };

            foreach (var path in orderedPaths)
                await File.WriteAllTextAsync(path, Path.GetFileNameWithoutExtension(path));

            var entries = orderedPaths.Select(path => new LogFileEntry { FilePath = path }).ToList();
            foreach (var entry in entries)
                await fileRepo.AddAsync(entry);

            var logReader = new CoordinatedParallelLogReaderService(orderedPaths, targetConcurrency: 4);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);
            var group = Assert.Single(vm.Groups);
            group.Model.FileIds.AddRange(entries.Select(entry => entry.Id));

            var loadTask = vm.OpenGroupFilesAsync(group);
            await logReader.WaitForTargetConcurrencyAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsDashboardLoading);
            Assert.Empty(vm.Tabs);

            logReader.ReleaseBuilds();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(4, logReader.MaxObservedConcurrency);
            Assert.Equal(orderedPaths, vm.Tabs.Select(tab => tab.FilePath).ToArray());
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenGroupFilesAsync_ParallelDashboardLoad_DoesNotExceedConcurrencyLimit()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmParallelLimit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var coordinatedPaths = Enumerable.Range(1, 6)
                .Select(index => Path.Combine(testDir, $"file{index}.log"))
                .ToArray();
            foreach (var path in coordinatedPaths)
                await File.WriteAllTextAsync(path, $"content-{Path.GetFileNameWithoutExtension(path)}");

            var entries = coordinatedPaths.Select(path => new LogFileEntry { FilePath = path }).ToList();
            foreach (var entry in entries)
                await fileRepo.AddAsync(entry);

            var logReader = new CoordinatedParallelLogReaderService(coordinatedPaths, targetConcurrency: 4);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);
            var group = Assert.Single(vm.Groups);
            group.Model.FileIds.AddRange(entries.Select(entry => entry.Id));

            var loadTask = vm.OpenGroupFilesAsync(group);
            await logReader.WaitForTargetConcurrencyAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsDashboardLoading);
            Assert.Empty(vm.Tabs);
            Assert.Equal(4, logReader.MaxObservedConcurrency);

            logReader.ReleaseBuilds();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(coordinatedPaths, vm.Tabs.Select(tab => tab.FilePath).ToArray());
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenGroupFilesAsync_ParallelDashboardLoad_HonorsConfiguredConcurrency()
    {
        var fileRepo = new StubLogFileRepository();
        var settingsRepo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                DashboardLoadConcurrency = 2
            }
        };
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmParallelConfiguredLimit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var coordinatedPaths = Enumerable.Range(1, 4)
                .Select(index => Path.Combine(testDir, $"file{index}.log"))
                .ToArray();
            foreach (var path in coordinatedPaths)
                await File.WriteAllTextAsync(path, $"content-{Path.GetFileNameWithoutExtension(path)}");

            var entries = coordinatedPaths.Select(path => new LogFileEntry { FilePath = path }).ToList();
            foreach (var entry in entries)
                await fileRepo.AddAsync(entry);

            var logReader = new CoordinatedParallelLogReaderService(coordinatedPaths, targetConcurrency: 2);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                settingsRepo: settingsRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);
            var group = Assert.Single(vm.Groups);
            group.Model.FileIds.AddRange(entries.Select(entry => entry.Id));

            var loadTask = vm.OpenGroupFilesAsync(group);
            await logReader.WaitForTargetConcurrencyAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsDashboardLoading);
            Assert.Empty(vm.Tabs);
            Assert.Equal(2, logReader.MaxObservedConcurrency);

            logReader.ReleaseBuilds();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(coordinatedPaths, vm.Tabs.Select(tab => tab.FilePath).ToArray());
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenDashboardMemberFileAsync_WhenDashboardAlreadyLoading_IsIgnored()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmDashboardMemberResume_" + Guid.NewGuid().ToString("N")[..8]);
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

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(slowEntry.Id);
            dashboard.Model.FileIds.Add(fastEntry.Id);
            RefreshDashboardMemberFiles(dashboard, (slowEntry.Id, slowPath), (fastEntry.Id, fastPath));

            vm.ToggleGroupSelection(dashboard);
            var loadTask = vm.OpenGroupFilesAsync(dashboard);
            await logReader.WaitForBlockedBuildAsync();

            var member = dashboard.MemberFiles.Single(file => file.FilePath == fastPath);
            var memberOpenTask = vm.OpenDashboardMemberFileAsync(dashboard, member);
            await memberOpenTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsDashboardLoading);
            Assert.False(logReader.BlockedBuildCanceled);
            Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
            Assert.Null(vm.SelectedTab);
            Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.FilePath, fastPath, StringComparison.OrdinalIgnoreCase));

            logReader.ReleaseBlockedBuild();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, vm.Tabs.Count);
            Assert.NotNull(FindScopedTab(vm, slowPath, dashboard.Id));
            Assert.NotNull(FindScopedTab(vm, fastPath, dashboard.Id));
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenDashboardMemberFileAsync_WhenDashboardIsInactive_LoadsDashboardMonolithically_ThenSelectsClickedMember()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmDashboardMemberInactive_" + Guid.NewGuid().ToString("N")[..8]);
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

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(slowEntry.Id);
            dashboard.Model.FileIds.Add(fastEntry.Id);
            RefreshDashboardMemberFiles(dashboard, (slowEntry.Id, slowPath), (fastEntry.Id, fastPath));

            var member = dashboard.MemberFiles.Single(file => file.FilePath == fastPath);
            var memberOpenTask = vm.OpenDashboardMemberFileAsync(dashboard, member);

            await logReader.WaitForBlockedBuildAsync();

            Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
            Assert.True(vm.IsDashboardLoading);
            Assert.Null(vm.SelectedTab);
            Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.FilePath, fastPath, StringComparison.OrdinalIgnoreCase));
            Assert.False(logReader.BlockedBuildCanceled);

            logReader.ReleaseBlockedBuild();
            await memberOpenTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, vm.Tabs.Count);
            Assert.NotNull(FindScopedTab(vm, slowPath, dashboard.Id));
            Assert.NotNull(FindScopedTab(vm, fastPath, dashboard.Id));
            Assert.Equal(fastPath, vm.SelectedTab?.FilePath);
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenDashboardMemberFileAsync_WhenDashboardActive_SelectsExistingScopedTabWithoutOpeningNewTab()
    {
        var fileRepo = new StubLogFileRepository();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmDashboardMemberActive_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");

            var fileA = new LogFileEntry { FilePath = fileAPath };
            var fileB = new LogFileEntry { FilePath = fileBPath };
            await fileRepo.AddAsync(fileA);
            await fileRepo.AddAsync(fileB);

            var vm = CreateViewModel(fileRepo: fileRepo);
            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(fileA.Id);
            dashboard.Model.FileIds.Add(fileB.Id);
            RefreshDashboardMemberFiles(dashboard, (fileA.Id, fileAPath), (fileB.Id, fileBPath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenGroupFilesAsync(dashboard);

            var tabA = FindScopedTab(vm, fileAPath, dashboard.Id);
            var tabB = FindScopedTab(vm, fileBPath, dashboard.Id);
            vm.SelectedTab = tabA;
            var member = dashboard.MemberFiles.ToList().Single(file => file.FilePath == fileBPath);

            await vm.OpenDashboardMemberFileAsync(dashboard, member);

            Assert.Equal(2, vm.Tabs.Count);
            Assert.Same(tabB, vm.SelectedTab);
            Assert.Same(tabA, FindScopedTab(vm, fileAPath, dashboard.Id));
            Assert.Same(tabB, FindScopedTab(vm, fileBPath, dashboard.Id));
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenDashboardMemberFileAsync_WhenDashboardActive_ClosedMemberTab_ReopensAndSelectsMember()
    {
        var fileRepo = new StubLogFileRepository();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmDashboardMemberClosed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");

            var fileA = new LogFileEntry { FilePath = fileAPath };
            var fileB = new LogFileEntry { FilePath = fileBPath };
            await fileRepo.AddAsync(fileA);
            await fileRepo.AddAsync(fileB);

            var vm = CreateViewModel(fileRepo: fileRepo);
            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(fileA.Id);
            dashboard.Model.FileIds.Add(fileB.Id);
            RefreshDashboardMemberFiles(dashboard, (fileA.Id, fileAPath), (fileB.Id, fileBPath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenGroupFilesAsync(dashboard);

            var remainingTab = FindScopedTab(vm, fileAPath, dashboard.Id);
            var closedTab = FindScopedTab(vm, fileBPath, dashboard.Id);
            await vm.CloseTabCommand.ExecuteAsync(closedTab);
            var member = dashboard.MemberFiles.ToList().Single(file => file.FilePath == fileBPath);

            await vm.OpenDashboardMemberFileAsync(dashboard, member);

            Assert.Equal(2, vm.Tabs.Count);
            Assert.Same(remainingTab, FindScopedTab(vm, fileAPath, dashboard.Id));
            Assert.Equal(fileBPath, vm.SelectedTab?.FilePath);
            Assert.NotNull(FindScopedTab(vm, fileBPath, dashboard.Id));
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadDashboardAsync_WhenDashboardInactive_ActivatesScopeAndLoadsAllMembers()
    {
        var fileRepo = new StubLogFileRepository();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmReloadDashboardInactive_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");

            var fileA = new LogFileEntry { FilePath = fileAPath };
            var fileB = new LogFileEntry { FilePath = fileBPath };
            await fileRepo.AddAsync(fileA);
            await fileRepo.AddAsync(fileB);

            var vm = CreateViewModel(fileRepo: fileRepo);
            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(fileA.Id);
            dashboard.Model.FileIds.Add(fileB.Id);
            RefreshDashboardMemberFiles(dashboard, (fileA.Id, fileAPath), (fileB.Id, fileBPath));

            await vm.ReloadDashboardAsync(dashboard);

            Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
            Assert.Equal(2, vm.Tabs.Count);
            Assert.NotNull(FindScopedTab(vm, fileAPath, dashboard.Id));
            Assert.NotNull(FindScopedTab(vm, fileBPath, dashboard.Id));
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadDashboardAsync_WhenDashboardActive_FlushesScopedTabsBeforeReopenAndClearsSelectedTabState()
    {
        var fileRepo = new StubLogFileRepository();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmReloadDashboardActive_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");

            var fileA = new LogFileEntry { FilePath = fileAPath };
            var fileB = new LogFileEntry { FilePath = fileBPath };
            await fileRepo.AddAsync(fileA);
            await fileRepo.AddAsync(fileB);

            var vm = CreateViewModel(fileRepo: fileRepo);
            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(fileA.Id);
            dashboard.Model.FileIds.Add(fileB.Id);
            RefreshDashboardMemberFiles(dashboard, (fileA.Id, fileAPath), (fileB.Id, fileBPath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenGroupFilesAsync(dashboard);
            var existingTabA = FindScopedTab(vm, fileAPath, dashboard.Id);
            var existingTabB = FindScopedTab(vm, fileBPath, dashboard.Id);
            vm.SelectedTab = existingTabB;

            await vm.ReloadDashboardAsync(dashboard);

            var reopenedTabA = FindScopedTab(vm, fileAPath, dashboard.Id);
            var reopenedTabB = FindScopedTab(vm, fileBPath, dashboard.Id);
            Assert.NotSame(existingTabA, reopenedTabA);
            Assert.NotSame(existingTabB, reopenedTabB);
            Assert.Equal(2, vm.Tabs.Count);
            Assert.Equal(fileAPath, vm.SelectedTab?.FilePath);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadDashboardAsync_ClearsScopedSearchAndFilterState()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmReloadReset_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");

            await vm.InitializeAsync();
            await vm.OpenFilePathAsync(fileAPath);
            await vm.OpenFilePathAsync(fileBPath);
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            var adHocTabA = vm.Tabs.Single(tab => tab.FilePath == fileAPath && tab.IsAdHocScope);
            var adHocTabB = vm.Tabs.Single(tab => tab.FilePath == fileBPath && tab.IsAdHocScope);
            dashboard.Model.FileIds.Add(adHocTabA.FileId);
            dashboard.Model.FileIds.Add(adHocTabB.FileId);
            RefreshDashboardMemberFiles(
                dashboard,
                (adHocTabA.FileId, adHocTabA.FilePath),
                (adHocTabB.FileId, adHocTabB.FilePath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenGroupFilesAsync(dashboard);
            var dashboardTabA = FindScopedTab(vm, fileAPath, dashboard.Id);
            var dashboardTabB = FindScopedTab(vm, fileBPath, dashboard.Id);
            vm.SelectedTab = dashboardTabB;

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Hits = [new SearchHit { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                }
            ];

            vm.SearchPanel.Query = "scope";
            vm.SearchPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.SearchPanel.Results.Count);

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 3, LineText = "Filter hit", MatchStart = 0, MatchLength = 6 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Error = "disk failed"
                }
            ];

            vm.FilterPanel.Query = "filter";
            vm.FilterPanel.IsAllOpenTabsTarget = true;
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
            Assert.Single(vm.FilterPanel.Warnings);
            Assert.True(dashboardTabA.IsFilterActive);

            await vm.ReloadDashboardAsync(dashboard);

            var reloadedTabA = FindScopedTab(vm, fileAPath, dashboard.Id);
            var reloadedTabB = FindScopedTab(vm, fileBPath, dashboard.Id);
            Assert.Equal(fileAPath, vm.SelectedTab?.FilePath);
            Assert.NotSame(dashboardTabA, reloadedTabA);
            Assert.NotSame(dashboardTabB, reloadedTabB);
            Assert.Empty(vm.SearchPanel.Query);
            Assert.Empty(vm.SearchPanel.Results);
            Assert.Empty(vm.FilterPanel.Query);
            Assert.Empty(vm.FilterPanel.Warnings);
            Assert.False(reloadedTabA.IsFilterActive);
            Assert.False(reloadedTabB.IsFilterActive);
        }
        finally
        {
            vm.Dispose();
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadDashboardAsync_PreservesRecentScopeStateBeforeReopenWithoutFilterState()
    {
        var reader = new StubLogReaderService();
        var vm = CreateViewModel(logReader: reader);
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmReloadRecentState_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);
        try
        {
            await vm.InitializeAsync();

            var filePath = Path.Combine(testDir, "recent-dashboard.log");
            await File.WriteAllLinesAsync(
                filePath,
                Enumerable.Range(1, 200).Select(index => $"line {index}"));
            await vm.OpenFilePathAsync(filePath);
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            var adHocTab = Assert.Single(vm.Tabs);
            dashboard.Model.FileIds.Add(adHocTab.FileId);
            RefreshDashboardMemberFiles(dashboard, (adHocTab.FileId, adHocTab.FilePath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenGroupFilesAsync(dashboard);
            var originalTab = FindScopedTab(vm, filePath, dashboard.Id);
            vm.TogglePinTab(originalTab);
            await ChangeEncodingAndWaitForLoadAsync(originalTab, FileEncoding.Utf16);
            vm.GlobalAutoScrollEnabled = false;
            await originalTab.ApplyFilterAsync(
                Enumerable.Range(1, 120).ToArray(),
                "Filter active: 120 matching lines.",
                new SearchRequest
                {
                    Query = "Line",
                    FilePaths = new List<string> { filePath }
                },
                hasParseableTimestamps: false);
            await originalTab.LoadViewportAsync(10, originalTab.ViewportLineCount);
            originalTab.SetNavigateTargetLine(42);

            await vm.ReloadDashboardAsync(dashboard);

            var reopenedTab = FindScopedTab(vm, filePath, dashboard.Id);
            Assert.NotSame(originalTab, reopenedTab);
            Assert.Equal(FileEncoding.Utf16, reopenedTab.Encoding);
            Assert.Equal(FileEncoding.Utf16, reopenedTab.EffectiveEncoding);
            Assert.True(reopenedTab.IsPinned);
            Assert.False(reopenedTab.IsFilterActive);
            Assert.Equal(11, reopenedTab.VisibleLines.First().LineNumber);
            Assert.Equal(42, reopenedTab.NavigateToLineNumber);
        }
        finally
        {
            vm.Dispose();
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadDashboardAsync_WhenGroupRefreshFails_PreservesScopedTabsAndState()
    {
        var fileRepo = new ThrowingLogFileRepository();
        var groupRepo = new ThrowingLogGroupRepository();
        var search = new RecordingSearchService();
        var messageBoxService = new StubMessageBoxService();
        using var vm = CreateViewModel(
            fileRepo: fileRepo,
            groupRepo: groupRepo,
            searchService: search,
            messageBoxService: messageBoxService);
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmReloadGroupFailure_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");

            var fileA = new LogFileEntry { FilePath = fileAPath };
            var fileB = new LogFileEntry { FilePath = fileBPath };
            await fileRepo.AddAsync(fileA);
            await fileRepo.AddAsync(fileB);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(fileA.Id);
            dashboard.Model.FileIds.Add(fileB.Id);
            RefreshDashboardMemberFiles(dashboard, (fileA.Id, fileAPath), (fileB.Id, fileBPath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenGroupFilesAsync(dashboard);
            var dashboardTabA = FindScopedTab(vm, fileAPath, dashboard.Id);
            var dashboardTabB = FindScopedTab(vm, fileBPath, dashboard.Id);
            vm.SelectedTab = dashboardTabB;

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Hits = [new SearchHit { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                }
            ];

            vm.SearchPanel.Query = "scope";
            vm.SearchPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 3, LineText = "Filter hit", MatchStart = 0, MatchLength = 6 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Error = "disk failed"
                }
            ];

            vm.FilterPanel.Query = "filter";
            vm.FilterPanel.IsAllOpenTabsTarget = true;
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

            groupRepo.ThrowOnGetAll = true;

            await vm.ReloadDashboardAsync(dashboard);

            Assert.Same(dashboardTabA, FindScopedTab(vm, fileAPath, dashboard.Id));
            Assert.Same(dashboardTabB, FindScopedTab(vm, fileBPath, dashboard.Id));
            Assert.Same(dashboardTabB, vm.SelectedTab);
            Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
            Assert.Equal("scope", vm.SearchPanel.Query);
            Assert.Equal(2, vm.SearchPanel.Results.Count);
            Assert.Equal("filter", vm.FilterPanel.Query);
            Assert.Single(vm.FilterPanel.Warnings);
            Assert.True(dashboardTabA.IsFilterActive);
            Assert.False(dashboardTabB.IsFilterActive);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadDashboardAsync_WhenMemberRefreshFails_PreservesScopedTabsAndState()
    {
        var fileRepo = new ThrowingLogFileRepository();
        var groupRepo = new ThrowingLogGroupRepository();
        var search = new RecordingSearchService();
        var messageBoxService = new StubMessageBoxService();
        using var vm = CreateViewModel(
            fileRepo: fileRepo,
            groupRepo: groupRepo,
            searchService: search,
            messageBoxService: messageBoxService);
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmReloadMemberFailure_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");

            var fileA = new LogFileEntry { FilePath = fileAPath };
            var fileB = new LogFileEntry { FilePath = fileBPath };
            await fileRepo.AddAsync(fileA);
            await fileRepo.AddAsync(fileB);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(fileA.Id);
            dashboard.Model.FileIds.Add(fileB.Id);
            RefreshDashboardMemberFiles(dashboard, (fileA.Id, fileAPath), (fileB.Id, fileBPath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenGroupFilesAsync(dashboard);
            var dashboardTabA = FindScopedTab(vm, fileAPath, dashboard.Id);
            var dashboardTabB = FindScopedTab(vm, fileBPath, dashboard.Id);
            vm.SelectedTab = dashboardTabB;

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Hits = [new SearchHit { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                }
            ];

            vm.SearchPanel.Query = "scope";
            vm.SearchPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 3, LineText = "Filter hit", MatchStart = 0, MatchLength = 6 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Error = "disk failed"
                }
            ];

            vm.FilterPanel.Query = "filter";
            vm.FilterPanel.IsAllOpenTabsTarget = true;
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

            fileRepo.ThrowOnGetByIds = true;

            await vm.ReloadDashboardAsync(dashboard);

            Assert.Same(dashboardTabA, FindScopedTab(vm, fileAPath, dashboard.Id));
            Assert.Same(dashboardTabB, FindScopedTab(vm, fileBPath, dashboard.Id));
            Assert.Same(dashboardTabB, vm.SelectedTab);
            Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
            Assert.Equal("scope", vm.SearchPanel.Query);
            Assert.Equal(2, vm.SearchPanel.Results.Count);
            Assert.Equal("filter", vm.FilterPanel.Query);
            Assert.Single(vm.FilterPanel.Warnings);
            Assert.True(dashboardTabA.IsFilterActive);
            Assert.False(dashboardTabB.IsFilterActive);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadDashboardAsync_ReappliesActiveDashboardModifier()
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

        vm.ToggleGroupSelection(dashboard);
        await vm.OpenGroupFilesAsync(dashboard);
        Assert.Contains(vm.Tabs, tab => string.Equals(tab.FilePath, effectivePath, StringComparison.OrdinalIgnoreCase));

        await vm.ReloadDashboardAsync(dashboard);

        Assert.Contains(vm.Tabs, tab =>
            string.Equals(tab.FilePath, effectivePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Tabs, tab =>
            string.Equals(tab.FilePath, basePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReloadDashboardAsync_BeginsFreezeBeforeScopedFlushAndIgnoresDashboardSwitch()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new ArmableBlockingLogGroupRepository();
        var search = new RecordingSearchService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmReloadFreeze_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fileAPath = Path.Combine(testDir, "a.log");
            var fileBPath = Path.Combine(testDir, "b.log");
            var fileCPath = Path.Combine(testDir, "c.log");
            await File.WriteAllTextAsync(fileAPath, "a");
            await File.WriteAllTextAsync(fileBPath, "b");
            await File.WriteAllTextAsync(fileCPath, "c");

            var fileA = new LogFileEntry { FilePath = fileAPath };
            var fileB = new LogFileEntry { FilePath = fileBPath };
            var fileC = new LogFileEntry { FilePath = fileCPath };
            await fileRepo.AddAsync(fileA);
            await fileRepo.AddAsync(fileB);
            await fileRepo.AddAsync(fileC);

            await groupRepo.AddAsync(new LogGroup
            {
                Id = "dashboard-a",
                Name = "Dashboard A",
                Kind = LogGroupKind.Dashboard,
                FileIds = new List<string> { fileA.Id, fileB.Id }
            });
            await groupRepo.AddAsync(new LogGroup
            {
                Id = "dashboard-b",
                Name = "Dashboard B",
                Kind = LogGroupKind.Dashboard,
                FileIds = new List<string> { fileC.Id }
            });

            using var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo, searchService: search);
            await vm.InitializeAsync();

            var dashboardA = vm.Groups.Single(group => string.Equals(group.Id, "dashboard-a", StringComparison.Ordinal));
            var dashboardB = vm.Groups.Single(group => string.Equals(group.Id, "dashboard-b", StringComparison.Ordinal));

            vm.ToggleGroupSelection(dashboardA);
            await vm.OpenGroupFilesAsync(dashboardA);

            var dashboardTabA = FindScopedTab(vm, fileAPath, dashboardA.Id);
            var dashboardTabB = FindScopedTab(vm, fileBPath, dashboardA.Id);
            vm.SelectedTab = dashboardTabB;

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Hits = [new SearchHit { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }],
                    HasParseableTimestamps = true
                }
            ];

            vm.SearchPanel.Query = "scope";
            vm.SearchPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fileAPath,
                    Hits = [new SearchHit { LineNumber = 3, LineText = "Filter hit", MatchStart = 0, MatchLength = 6 }],
                    HasParseableTimestamps = true
                },
                new SearchResult
                {
                    FilePath = fileBPath,
                    Error = "disk failed"
                }
            ];

            vm.FilterPanel.Query = "filter";
            vm.FilterPanel.IsAllOpenTabsTarget = true;
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

            groupRepo.ArmBlocking();
            var reloadTask = vm.ReloadDashboardAsync(dashboardA);
            await groupRepo.WaitForBlockedGetAllAsync();

            Assert.True(vm.IsDashboardLoading);
            Assert.Equal(dashboardA.Id, vm.ActiveDashboardId);
            Assert.Same(dashboardTabA, FindScopedTab(vm, fileAPath, dashboardA.Id));
            Assert.Same(dashboardTabB, FindScopedTab(vm, fileBPath, dashboardA.Id));
            Assert.Same(dashboardTabB, vm.SelectedTab);
            Assert.Equal("scope", vm.SearchPanel.Query);
            Assert.Equal(2, vm.SearchPanel.Results.Count);
            Assert.Equal("filter", vm.FilterPanel.Query);
            Assert.Single(vm.FilterPanel.Warnings);
            Assert.True(dashboardTabA.IsFilterActive);
            Assert.False(dashboardTabB.IsFilterActive);

            vm.ToggleGroupSelection(dashboardB);

            Assert.Equal(dashboardA.Id, vm.ActiveDashboardId);
            Assert.False(dashboardB.IsSelected);

            groupRepo.ReleaseBlockedGetAll();
            await reloadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var reopenedTabA = FindScopedTab(vm, fileAPath, dashboardA.Id);
            var reopenedTabB = FindScopedTab(vm, fileBPath, dashboardA.Id);
            Assert.NotSame(dashboardTabA, reopenedTabA);
            Assert.NotSame(dashboardTabB, reopenedTabB);
            Assert.Equal(dashboardA.Id, vm.ActiveDashboardId);
            Assert.Equal(fileAPath, vm.SelectedTab?.FilePath);
            Assert.Empty(vm.SearchPanel.Query);
            Assert.Empty(vm.SearchPanel.Results);
            Assert.Empty(vm.FilterPanel.Query);
            Assert.Empty(vm.FilterPanel.Warnings);
            Assert.False(reopenedTabA.IsFilterActive);
            Assert.False(reopenedTabB.IsFilterActive);
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadCommands_DuringDashboardLoad_AreIgnored()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmReloadDuringLoad_" + Guid.NewGuid().ToString("N")[..8]);
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

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(slowEntry.Id);
            dashboard.Model.FileIds.Add(fastEntry.Id);
            RefreshDashboardMemberFiles(dashboard, (slowEntry.Id, slowPath), (fastEntry.Id, fastPath));

            vm.ToggleGroupSelection(dashboard);
            var loadTask = vm.OpenGroupFilesAsync(dashboard);
            await logReader.WaitForBlockedBuildAsync();

            await vm.ReloadDashboardAsync(dashboard);

            Assert.True(vm.IsDashboardLoading);
            Assert.Equal(2, logReader.BuildIndexCallCount);
            Assert.False(logReader.BlockedBuildCanceled);

            logReader.ReleaseBlockedBuild();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, logReader.BuildIndexCallCount);
            Assert.Equal(2, vm.Tabs.Count);
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task TabCloseAndPin_DuringDashboardLoad_AreIgnored()
    {
        var fileRepo = new StubLogFileRepository();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmTabFreeze_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fastPath = Path.Combine(testDir, "fast.log");
            var slowPath = Path.Combine(testDir, "slow.log");
            await File.WriteAllTextAsync(fastPath, "fast");
            await File.WriteAllTextAsync(slowPath, "slow");

            var fastEntry = new LogFileEntry { FilePath = fastPath };
            var slowEntry = new LogFileEntry { FilePath = slowPath };
            await fileRepo.AddAsync(fastEntry);
            await fileRepo.AddAsync(slowEntry);

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(fastEntry.Id);
            dashboard.Model.FileIds.Add(slowEntry.Id);
            RefreshDashboardMemberFiles(dashboard, (fastEntry.Id, fastPath), (slowEntry.Id, slowPath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenFilePathAsync(fastPath);
            var existingTab = FindScopedTab(vm, fastPath, dashboard.Id);

            var loadTask = vm.OpenGroupFilesAsync(dashboard);
            await logReader.WaitForBlockedBuildAsync();

            vm.TogglePinTab(existingTab);
            await vm.CloseTabCommand.ExecuteAsync(existingTab);

            Assert.False(existingTab.IsPinned);
            Assert.Contains(existingTab, vm.Tabs);

            logReader.ReleaseBlockedBuild();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, vm.Tabs.Count);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAndFilterActions_DuringDashboardLoad_AreIgnored_WhileDraftsRemainEditable()
    {
        var fileRepo = new StubLogFileRepository();
        var search = new RecordingSearchService();
        var encodingDetectionService = new StubEncodingDetectionService();
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmSearchFilterFreeze_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);

        try
        {
            var fastPath = Path.Combine(testDir, "fast.log");
            var slowPath = Path.Combine(testDir, "slow.log");
            await File.WriteAllTextAsync(fastPath, "Line one");
            await File.WriteAllTextAsync(slowPath, "Line two");

            var fastEntry = new LogFileEntry { FilePath = fastPath };
            var slowEntry = new LogFileEntry { FilePath = slowPath };
            await fileRepo.AddAsync(fastEntry);
            await fileRepo.AddAsync(slowEntry);

            search.NextResults =
            [
                new SearchResult
                {
                    FilePath = fastPath,
                    Hits =
                    [
                        new SearchHit { LineNumber = 1, LineText = "Line one", MatchStart = 0, MatchLength = 4 }
                    ],
                    HasParseableTimestamps = true
                }
            ];
            search.NextResult = new SearchResult
            {
                FilePath = fastPath,
                Hits =
                [
                    new SearchHit { LineNumber = 1, LineText = "Line one", MatchStart = 0, MatchLength = 4 }
                ],
                HasParseableTimestamps = true
            };

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
            var vm = CreateViewModel(
                fileRepo: fileRepo,
                logReader: logReader,
                searchService: search,
                encodingDetectionService: encodingDetectionService);

            await vm.InitializeAsync();
            await vm.CreateGroupCommand.ExecuteAsync(null);

            var dashboard = Assert.Single(vm.Groups);
            dashboard.Model.FileIds.Add(fastEntry.Id);
            dashboard.Model.FileIds.Add(slowEntry.Id);
            RefreshDashboardMemberFiles(dashboard, (fastEntry.Id, fastPath), (slowEntry.Id, slowPath));

            vm.ToggleGroupSelection(dashboard);
            await vm.OpenFilePathAsync(fastPath);

            vm.SearchPanel.Query = "Line";
            await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);
            Assert.Single(vm.SearchPanel.Results);

            vm.FilterPanel.Query = "Line";
            vm.FilterPanel.IsCurrentTabTarget = true;
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
            Assert.True(vm.SelectedTab!.IsFilterActive);

            var baselineSearchFilesCallCount = search.SearchFilesCallCount;
            var baselineSearchFileCallCount = search.SearchFileCallCount;

            var loadTask = vm.OpenGroupFilesAsync(dashboard);
            await logReader.WaitForBlockedBuildAsync();

            Assert.False(vm.SearchPanel.AreExecutionControlsEnabled);
            Assert.False(vm.SearchPanel.AreResultsInteractionEnabled);
            Assert.False(vm.FilterPanel.AreExecutionControlsEnabled);

            vm.SearchPanel.Query = "updated search";
            vm.FilterPanel.Query = "updated filter";
            await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null);
            vm.SearchPanel.ClearResultsCommand.Execute(null);
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
            await vm.FilterPanel.ClearFilterCommand.ExecuteAsync(null);

            Assert.Equal(baselineSearchFilesCallCount, search.SearchFilesCallCount);
            Assert.Equal(baselineSearchFileCallCount, search.SearchFileCallCount);
            Assert.Single(vm.SearchPanel.Results);
            Assert.True(vm.SelectedTab.IsFilterActive);
            Assert.Equal("updated search", vm.SearchPanel.Query);
            Assert.Equal("updated filter", vm.FilterPanel.Query);

            logReader.ReleaseBlockedBuild();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenGroupFilesAsync_SelectingBranch_DuringDashboardLoad_IsIgnored()
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

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
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

            Assert.False(logReader.BlockedBuildCanceled);
            Assert.Equal(slowDashboard.Id, vm.ActiveDashboardId);

            logReader.ReleaseBlockedBuild();
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(logReader.BlockedBuildCanceled);
            Assert.Equal(slowDashboard.Id, vm.ActiveDashboardId);
            Assert.Single(vm.Tabs);
            Assert.Equal(slowPath, vm.Tabs[0].FilePath);
            Assert.False(vm.IsDashboardLoading);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteGroupCommand_DeletingActiveDashboard_DuringDashboardLoad_IsIgnored()
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

            var logReader = new ReleasableBlockingLogReaderService(slowPath);
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

            Assert.False(logReader.BlockedBuildCanceled);
            Assert.Equal(slowDashboard.Id, vm.ActiveDashboardId);
            Assert.Single(vm.Groups);

            logReader.ReleaseBlockedBuild();
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(logReader.BlockedBuildCanceled);
            Assert.Equal(slowDashboard.Id, vm.ActiveDashboardId);
            Assert.Single(vm.Tabs);
            Assert.Single(vm.Groups);
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
                Assert.DoesNotContain("Do you want to continue?", message);
                Assert.Equal("Delete Dashboard?", caption);
                Assert.Equal(MessageBoxButton.YesNo, buttons);
                Assert.Equal(MessageBoxImage.None, image);
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
    public async Task ImportViewCommand_DuringDashboardLoad_IsIgnored()
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
            var logReader = new ReleasableBlockingLogReaderService(slowPath);
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
            Assert.Null(fileDialogService.LastOpenRequest);
            Assert.Null(groupRepo.LastImportPath);
            Assert.False(logReader.BlockedBuildCanceled);
            Assert.True(vm.IsDashboardLoading);
            Assert.Equal(currentDashboard.Id, vm.ActiveDashboardId);

            logReader.ReleaseBlockedBuild();
            await slowLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(logReader.BlockedBuildCanceled);
            Assert.False(vm.IsDashboardLoading);
            Assert.Equal(currentDashboard.Id, vm.ActiveDashboardId);
            Assert.Single(vm.Tabs);
            Assert.Equal(slowPath, vm.Tabs[0].FilePath);
            Assert.Equal(new[] { "Current Dashboard" }, vm.Groups.Select(group => group.Name).ToArray());
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
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        vm.ToggleGroupSelection(g2);
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        vm.ToggleGroupSelection(g1);

        var tabA = FindScopedTab(vm, @"C:\test\a.log", g1.Id);
        var tabB = FindScopedTab(vm, @"C:\test\b.log", g2.Id);
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

        vm.ToggleGroupSelection(g1);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        vm.ToggleGroupSelection(g2);
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tabB = FindScopedTab(vm, @"C:\test\b.log", g2.Id);
        Assert.True(tabB.IsVisible);
        Assert.False(tabB.IsSuspended);
        Assert.Contains(@"C:\test\b.log", tailService.ActiveFiles);
    }

    [Fact]
    public async Task LifecycleMaintenance_HiddenDuplicateDashboardTabs_DoNotSuspendVisibleTwin()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\shared.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboardA = vm.Groups[0];
        var dashboardB = vm.Groups[1];
        var adHocTab = vm.Tabs.Single(tab => string.Equals(tab.FilePath, @"C:\test\shared.log", StringComparison.OrdinalIgnoreCase));
        dashboardA.Model.FileIds.Add(adHocTab.FileId);
        dashboardB.Model.FileIds.Add(adHocTab.FileId);

        vm.ToggleGroupSelection(dashboardA);
        await vm.OpenFilePathAsync(adHocTab.FilePath);
        var dashboardTabA = FindScopedTab(vm, adHocTab.FilePath, dashboardA.Id);

        vm.ToggleGroupSelection(dashboardB);
        await vm.OpenFilePathAsync(adHocTab.FilePath);
        var dashboardTabB = FindScopedTab(vm, adHocTab.FilePath, dashboardB.Id);

        vm.ToggleGroupSelection(dashboardA);

        Assert.True(dashboardTabA.IsVisible);
        Assert.False(dashboardTabB.IsVisible);
        Assert.Contains(dashboardTabA.FilePath, tailService.ActiveFiles);

        vm.RunTabLifecycleMaintenance();

        Assert.True(dashboardTabA.IsVisible);
        Assert.False(dashboardTabA.IsSuspended);
        Assert.DoesNotContain(dashboardTabA, vm.Tabs.Where(tab => !tab.IsVisible && string.Equals(tab.ScopeDashboardId, dashboardA.Id, StringComparison.Ordinal)));
        Assert.Contains(dashboardTabA.FilePath, tailService.ActiveFiles);
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

        vm.ToggleGroupSelection(g1);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        vm.ToggleGroupSelection(g2);
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tabA = FindScopedTab(vm, @"C:\test\a.log", g1.Id);
        tabA.IsPinned = true;

        vm.RunTabLifecycleMaintenance();

        Assert.Equal(2, vm.Tabs.Count); // pinned dashboard tab is preserved

        tabA.IsPinned = false;
        vm.RunTabLifecycleMaintenance();

        Assert.Single(vm.Tabs);
        Assert.DoesNotContain(vm.Tabs, tab => string.Equals(tab.ScopeDashboardId, g1.Id, StringComparison.Ordinal));
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

        vm.ToggleGroupSelection(g1);
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        vm.ToggleGroupSelection(g2);
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tabA = FindScopedTab(vm, @"C:\test\a.log", g1.Id);
        var openOrder = GetOpenOrderMap(vm);
        var pinOrder = GetPinOrderMap(vm);
        Assert.Contains(tabA.TabInstanceId, openOrder.Keys);

        vm.RunTabLifecycleMaintenance();

        Assert.DoesNotContain(tabA.TabInstanceId, openOrder.Keys);
        Assert.DoesNotContain(tabA.TabInstanceId, pinOrder.Keys);
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

        var monitors = new List<Task>();
        foreach (var tab in vm.Tabs)
        {
            var disposeCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.ActiveSession.DebugLineIndexDisposeTask = disposeCompleted.Task;

            monitors.Add(Task.Run(async () =>
            {
                while (tab.ActiveSession.DebugIsDisposed == 0)
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
    public async Task ApplyDashboardFileDropAsync_WhenRecoveredStoreFailsAgain_ShowsFriendlyRecoveryError()
    {
        var fileRepo = new JsonLogFileRepository();
        var sourceEntry = await fileRepo.GetOrCreateByPathAsync(@"C:\logs\source.log");
        var targetEntry = await fileRepo.GetOrCreateByPathAsync(@"C:\logs\target.log");

        var groupRepo = new JsonLogGroupRepository(fileRepo);
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "dashboard-1",
            Name = "Source",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 0,
            FileIds = new List<string> { sourceEntry.Id }
        });
        await groupRepo.AddAsync(new LogGroup
        {
            Id = "dashboard-2",
            Name = "Target",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 1,
            FileIds = new List<string> { targetEntry.Id }
        });

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
            groupRepo: groupRepo,
            messageBoxService: messageBoxService,
            persistedStateRecoveryCoordinator: recoveryCoordinator);
        await vm.InitializeAsync();

        WriteInvalidStoreFile("loggroups.json");

        var source = vm.Groups.Single(group => group.Id == "dashboard-1");
        var target = vm.Groups.Single(group => group.Id == "dashboard-2");
        await vm.ApplyDashboardFileDropAsync(
            source,
            target,
            sourceEntry.Id,
            targetEntry.Id,
            DropPlacement.Before);

        Assert.Equal("LogReader Recovery Failed", messageBoxService.LastCaption);
        Assert.Contains("could not recover", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loggroups.json", messageBoxService.LastMessage, StringComparison.OrdinalIgnoreCase);
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

        await CreateDashboardHost(vm).OpenFilePathInScopeAsync(existingEntry.FilePath, scopeDashboardId: null);

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
    }

    [Fact]
    public void ToggleFocusMode_TogglesDashboardPaneOnly()
    {
        var vm = CreateViewModel();

        vm.ToggleFocusModeCommand.Execute(null);
        Assert.False(vm.IsGroupsPanelOpen);

        vm.ToggleFocusModeCommand.Execute(null);
        Assert.True(vm.IsGroupsPanelOpen);
    }

    [Fact]
    public void RememberPanelSizes_IgnoresSmallValues()
    {
        var vm = CreateViewModel();

        vm.RememberGroupsPanelWidth(280);
        vm.RememberSearchPanelHeight(410);
        vm.RememberGroupsPanelWidth(MainViewModel.GroupsPanelSnapThreshold - 1);
        vm.RememberSearchPanelHeight(20);

        Assert.Equal(280, vm.GroupsPanelWidth);
        Assert.Equal(410, vm.SearchPanelHeight);
    }

    [Fact]
    public void LifecycleScheduler_WhenEnabled_SchedulesAndDisposesRecurringMaintenance()
    {
        var scheduler = new StubTabLifecycleScheduler();
        var vm = CreateViewModel(enableLifecycleTimer: true, tabLifecycleScheduler: scheduler);

        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.LastDueTime);
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.LastInterval);
        Assert.NotNull(scheduler.LastCallback);

        vm.BeginShutdown();

        Assert.Equal(1, scheduler.DisposeCallCount);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 4000, int pollIntervalMs = 25)
    {
        var startedAt = DateTime.UtcNow;
        while (!TryEvaluateCondition(condition) && (DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            await Task.Delay(pollIntervalMs);

        Assert.True(TryEvaluateCondition(condition), "Timed out waiting for condition.");
    }

    private static bool TryEvaluateCondition(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
