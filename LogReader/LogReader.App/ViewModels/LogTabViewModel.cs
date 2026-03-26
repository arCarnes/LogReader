namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Helpers;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class LogTabViewModel : ObservableObject, IDisposable
{
    private const int StickyScrollBarMaximum = 1000;
    private const int StickyScrollBarViewportSize = 100;

    public sealed partial class EncodingOptionItem : ObservableObject
    {
        public FileEncoding Value { get; init; }

        [ObservableProperty]
        private string _label = string.Empty;
    }

    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly SemaphoreSlim _lineIndexLock = new(1, 1);
    private readonly LogViewportService _viewportService;
    private readonly LogTailCoordinator _tailCoordinator;
    private AppSettings _settings;
    private LineIndex? _lineIndex;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _navCts;
    private Task? _lineIndexDisposeTask;
    private int _isDisposed;
    private int _shutdownStarted;
    private readonly LogFilterSession _filterSession = new();

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;

    internal bool IsShutdownOrDisposed => IsShuttingDown || Volatile.Read(ref _isDisposed) != 0;
    internal AppSettings CurrentSettings => _settings;
    internal string? ActiveFilterStatusText => _filterSession.ActiveFilterStatusText;
    internal bool HasNoLineIndex => Volatile.Read(ref _lineIndex) == null;

    private string _fileId;
    public string FileId
    {
        get => _fileId;
        private set => SetProperty(ref _fileId, value);
    }
    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);

    [ObservableProperty]
    private FileEncoding _encoding = FileEncoding.Auto;

    private FileEncoding _effectiveEncoding = FileEncoding.Utf8;
    public FileEncoding EffectiveEncoding
    {
        get => _effectiveEncoding;
        private set => SetProperty(ref _effectiveEncoding, value);
    }

    private string _encodingStatusText = "Auto -> UTF-8 (fallback)";
    public string EncodingStatusText
    {
        get => _encodingStatusText;
        private set => SetProperty(ref _encodingStatusText, value);
    }

    [ObservableProperty]
    private bool _autoScrollEnabled = true;

    [ObservableProperty]
    private int _totalLines;

    [ObservableProperty]
    private int _navigateToLineNumber = -1;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasLoadError;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isSuspended;

    [ObservableProperty]
    private DateTime _lastVisibleAtUtc = DateTime.UtcNow;

    [ObservableProperty]
    private DateTime _lastHiddenAtUtc = DateTime.MinValue;

    // The currently visible lines (virtualized window)
    public ObservableCollection<LogLineViewModel> VisibleLines { get; } = new();

    private EncodingOptionItem AutoEncodingOption { get; }

    public IReadOnlyList<EncodingOptionItem> EncodingOptions { get; }

    public string SelectedEncodingDisplayLabel => Encoding == FileEncoding.Auto
        ? $"Auto ({EncodingHelper.GetEncodingDisplayName(EffectiveEncoding)})"
        : EncodingHelper.GetEncodingDisplayName(Encoding);

    public int ViewportLineCount => _viewportService.ViewportLineCount;
    public bool IsFilterActive => _filterSession.IsActive;
    public int FilteredLineCount => _filterSession.FilteredLineCount;
    public int DisplayLineCount => IsFilterActive ? FilteredLineCount : TotalLines;
    public int MaxScrollPosition => Math.Max(0, DisplayLineCount - _viewportService.ViewportLineCount);
    public int ScrollBarValue => AutoScrollEnabled ? StickyScrollBarMaximum : ScrollPosition;
    public int ScrollBarMaximum => AutoScrollEnabled ? StickyScrollBarMaximum : MaxScrollPosition;
    public int ScrollBarViewportSize => AutoScrollEnabled ? StickyScrollBarViewportSize : ViewportLineCount;
    private int _searchContentVersion;
    internal int SearchContentVersion
    {
        get => _searchContentVersion;
        private set => SetProperty(ref _searchContentVersion, value);
    }

    [ObservableProperty]
    private int _scrollPosition;

    public LogTabViewModel(
        string fileId,
        string filePath,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        AppSettings settings,
        bool skipInitialEncodingResolution = false)
    {
        _fileId = fileId;
        FilePath = filePath;
        _logReader = logReader;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
        _settings = settings;
        _viewportService = new LogViewportService(this, _filterSession);
        _tailCoordinator = new LogTailCoordinator(this, tailService);
        AutoEncodingOption = new EncodingOptionItem { Value = FileEncoding.Auto, Label = "Auto (UTF-8)" };
        EncodingOptions = new[]
        {
            AutoEncodingOption,
            new EncodingOptionItem { Value = FileEncoding.Utf8, Label = "UTF-8" },
            new EncodingOptionItem { Value = FileEncoding.Utf16, Label = "UTF-16" },
            new EncodingOptionItem { Value = FileEncoding.Utf16Be, Label = "UTF-16 BE" },
            new EncodingOptionItem { Value = FileEncoding.Ansi, Label = "ANSI" }
        };

        if (!skipInitialEncodingResolution)
            ResolveEffectiveEncoding();
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    internal void UpdateFileId(string fileId) => FileId = fileId;

    internal void UpdateViewportLineCount(int count)
        => _viewportService.UpdateViewportLineCount(count);

    public Task<bool> RefreshViewportAsync()
        => _viewportService.RefreshViewportAsync();

    public async Task LoadAsync()
    {
        if (IsShutdownOrDisposed)
            return;

        // Cancel and dispose any in-flight load so we don't race.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoading = true;
        HasLoadError = false;
        StatusText = Encoding == FileEncoding.Auto ? "Detecting encoding..." : "Building index...";

        try
        {
            await ResolveEffectiveEncodingAsync(cts.Token);
            StatusText = "Building index...";

            LineIndex? oldIndex;
            await _lineIndexLock.WaitAsync();
            try
            {
                oldIndex = _lineIndex;
                _lineIndex = null;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            oldIndex?.Dispose();

            var newIndex = await BuildIndexOffUiAsync(EffectiveEncoding, cts.Token);
            await _lineIndexLock.WaitAsync();
            try
            {
                _lineIndex = newIndex;
                TotalLines = _lineIndex.LineCount;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            StatusText = IsFilterActive
                ? _filterSession.ActiveFilterStatusText ?? $"Filter active: {FilteredLineCount:N0} matching lines."
                : $"{TotalLines:N0} lines";

            if (IsShutdownOrDisposed)
            {
                IsSuspended = true;
                return;
            }

            // Load initial viewport
            var initialStart = IsFilterActive
                ? 0
                : Math.Max(0, TotalLines - _viewportService.ViewportLineCount);
            await LoadViewportAsync(initialStart, _viewportService.ViewportLineCount);

            if (IsShutdownOrDisposed)
            {
                IsSuspended = true;
                return;
            }

            // Start tailing
            _tailCoordinator.StartLoadedTailing();
            HasLoadError = false;
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a newer load; leave IsLoading/StatusText for the new one
        }
        catch (Exception ex)
        {
            HasLoadError = true;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsLoading = false;
        }
    }

    public Task<bool> LoadViewportAsync(int startLine, int count, CancellationToken ct = default)
        => _viewportService.LoadViewportAsync(startLine, count, ct);

    internal Task<bool> TryAppendTailLinesToViewportAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
        => _viewportService.TryAppendTailLinesToViewportAsync(previousTotalLines, updatedLineCount, ct);

    partial void OnTotalLinesChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayLineCount));
        RaiseScrollMetricsChanged();
    }

    partial void OnAutoScrollEnabledChanged(bool value)
    {
        RaiseScrollBarPropertiesChanged();
    }

    internal void RaiseViewportPropertiesChanged()
    {
        OnPropertyChanged(nameof(ViewportLineCount));
        RaiseScrollMetricsChanged();
    }

    partial void OnScrollPositionChanged(int value)
    {
        OnPropertyChanged(nameof(ScrollBarValue));
        if (_viewportService.IsSuppressingScrollChange || IsShutdownOrDisposed) return;
        _ = ScrollToLineAsync(value);
    }

    private Task ScrollToLineAsync(int startLine)
        => _viewportService.ScrollToLineAsync(startLine, _navCts);

    [RelayCommand]
    private async Task JumpToTop()
    {
        if (await _viewportService.JumpToTopAsync(_navCts))
            SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? (IsFilterActive ? -1 : 1));
    }

    [RelayCommand]
    private async Task JumpToBottom()
    {
        if (await _viewportService.JumpToBottomAsync(_navCts))
            SetNavigateTargetLine(VisibleLines.LastOrDefault()?.LineNumber ?? (IsFilterActive ? -1 : TotalLines));
    }

    public async Task NavigateToLineAsync(int lineNumber)
        => await _viewportService.NavigateToLineAsync(lineNumber, _navCts);

    partial void OnEncodingChanged(FileEncoding value)
    {
        if (IsShutdownOrDisposed)
            return;

        ResolveEffectiveEncoding();
        OnPropertyChanged(nameof(SelectedEncodingDisplayLabel));

        // If the tab hasn't started loading yet, the upcoming explicit LoadAsync will use the correct encoding.
        // If a load is already in progress, LoadAsync will cancel the old one and restart.
        if (Volatile.Read(ref _lineIndex) == null && !IsLoading) return;
        _tailCoordinator.StopForReload();
        _ = LoadAsync();
    }

    private async Task ResolveEffectiveEncodingAsync(CancellationToken ct)
    {
        var decision = Encoding == FileEncoding.Auto
            ? await Task.Run(() => _encodingDetectionService.ResolveEncodingDecision(FilePath, Encoding)).WaitAsync(ct)
            : EncodingHelper.ResolveManualEncodingDecision(Encoding);

        ApplyEncodingDecision(decision);
    }

    private void ResolveEffectiveEncoding()
    {
        var decision = _encodingDetectionService.ResolveEncodingDecision(FilePath, Encoding);
        ApplyEncodingDecision(decision);
    }

    private void ApplyEncodingDecision(EncodingHelper.EncodingDecision decision)
    {
        EffectiveEncoding = decision.ResolvedEncoding;
        EncodingStatusText = decision.StatusText;
        AutoEncodingOption.Label = $"Auto ({EncodingHelper.GetEncodingDisplayName(EffectiveEncoding)})";
        OnPropertyChanged(nameof(SelectedEncodingDisplayLabel));
    }

    public void OnBecameVisible()
    {
        if (IsShutdownOrDisposed)
            return;

        IsVisible = true;
        LastVisibleAtUtc = DateTime.UtcNow;
        _tailCoordinator.ResumeTailing();
    }

    public void OnBecameHidden()
    {
        if (IsShutdownOrDisposed)
            return;

        if (!IsVisible) return;
        IsVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
        _tailCoordinator.SuspendTailing();
    }

    public void SuspendTailing()
        => _tailCoordinator.SuspendTailing();

    public void ResumeTailing()
        => _tailCoordinator.ResumeTailing();

    public void ApplyVisibleTailingMode(int pollingIntervalMs)
        => _tailCoordinator.ApplyVisibleTailingMode(pollingIntervalMs);

    public async Task ApplyFilterAsync(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest? filterRequest = null,
        bool hasParseableTimestamps = false)
    {
        _filterSession.ApplyFilter(
            matchingLineNumbers,
            statusText,
            filterRequest,
            hasParseableTimestamps,
            TotalLines);
        RaiseFilterPropertiesChanged();

        var viewportApplied = await LoadViewportAsync(0, _viewportService.ViewportLineCount);
        if (viewportApplied)
            SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? -1);

        StatusText = statusText;
    }

    public async Task ClearFilterAsync()
    {
        if (!IsFilterActive)
            return;

        _filterSession.Clear();
        RaiseFilterPropertiesChanged();

        var viewportApplied = await LoadViewportAsync(
            Math.Max(0, TotalLines - _viewportService.ViewportLineCount),
            _viewportService.ViewportLineCount);
        if (viewportApplied)
            SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? (TotalLines > 0 ? 1 : -1));

        StatusText = $"{TotalLines:N0} lines";
    }

    internal async Task ApplyTailFilterForAppendedLinesAsync(int updatedLineCount, CancellationToken ct)
    {
        if (!IsFilterActive)
            return;

        var previousDisplayCount = DisplayLineCount;
        var wasAtBottom = _viewportService.ViewportStartLine >= Math.Max(0, previousDisplayCount - _viewportService.ViewportLineCount);

        LineIndex? lineIndexSnapshot;
        await _lineIndexLock.WaitAsync(ct);
        try
        {
            lineIndexSnapshot = _lineIndex;
        }
        finally
        {
            _lineIndexLock.Release();
        }

        if (lineIndexSnapshot == null)
            return;

        var filterUpdate = await _filterSession.ProcessAppendedLinesAsync(
            updatedLineCount,
            lineIndexSnapshot,
            EffectiveEncoding,
            ReadLinesOffUiAsync,
            ct);
        StatusText = filterUpdate.StatusText;
        if (!filterUpdate.HasChanges)
            return;

        RaiseFilterPropertiesChanged();
        if (!wasAtBottom)
            return;

        var updatedInPlace = TryAppendFilteredTailLinesToViewportInPlace(previousDisplayCount, filterUpdate.AddedMatchingLines);
        if (!updatedInPlace)
        {
            var viewportApplied = await LoadViewportAsync(
                Math.Max(0, DisplayLineCount - _viewportService.ViewportLineCount),
                _viewportService.ViewportLineCount,
                ct);
            if (!viewportApplied)
                return;
        }

    }

    private bool TryAppendFilteredTailLinesToViewportInPlace(
        int previousDisplayCount,
        IReadOnlyList<LogFilterSession.FilterTailMatch> addedMatchingLines)
        => _viewportService.TryAppendFilteredTailLinesToViewportInPlace(previousDisplayCount, addedMatchingLines);

    public Task ResumeTailingWithCatchUpAsync(int pollingIntervalMs)
        => _tailCoordinator.ResumeTailingWithCatchUpAsync(pollingIntervalMs);

    internal Task<bool> MoveViewportToBottomAsync()
        => _viewportService.JumpToBottomAsync(_navCts);

    internal void SetNavigateTargetLine(int lineNumber)
    {
        NavigateToLineNumber = -1;
        if (lineNumber > 0)
            NavigateToLineNumber = lineNumber;
    }

    internal void SetNavigateTargetLineIfUnchanged(int expectedCurrentLine, int lineNumber)
    {
        if (NavigateToLineNumber != expectedCurrentLine)
            return;

        SetNavigateTargetLine(lineNumber);
    }

    internal void ApplyVisibleLines(IReadOnlyList<LogLineViewModel> nextVisibleLines)
    {
        var sharedCount = Math.Min(VisibleLines.Count, nextVisibleLines.Count);
        for (var i = 0; i < sharedCount; i++)
            VisibleLines[i] = nextVisibleLines[i];

        for (var i = VisibleLines.Count - 1; i >= nextVisibleLines.Count; i--)
            VisibleLines.RemoveAt(i);

        for (var i = sharedCount; i < nextVisibleLines.Count; i++)
            VisibleLines.Add(nextVisibleLines[i]);
    }

    private Task<LineIndex> BuildIndexOffUiAsync(FileEncoding encoding, CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.BuildIndexAsync(FilePath, encoding, ct).ConfigureAwait(false), ct);

    internal Task<IReadOnlyList<string>> ReadLinesOffUiAsync(
        LineIndex lineIndex,
        int startLine,
        int count,
        FileEncoding encoding,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.ReadLinesAsync(FilePath, lineIndex, startLine, count, encoding, ct).ConfigureAwait(false), ct);

    internal Task<string> ReadLineOffUiAsync(
        LineIndex lineIndex,
        int lineNumber,
        FileEncoding encoding,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.ReadLineAsync(FilePath, lineIndex, lineNumber, encoding, ct).ConfigureAwait(false), ct);

    internal async Task<LineIndex?> GetLineIndexSnapshotAsync(CancellationToken ct = default)
    {
        await _lineIndexLock.WaitAsync(ct);
        try
        {
            return _lineIndex;
        }
        finally
        {
            _lineIndexLock.Release();
        }
    }

    internal void ReplaceNavigationCts(CancellationTokenSource cts)
    {
        _navCts = cts;
    }

    internal async Task<int?> UpdateLineIndexLineCountAsync(CancellationToken ct)
    {
        await _lineIndexLock.WaitAsync(ct);
        try
        {
            if (_lineIndex == null)
                return null;

            _lineIndex = await UpdateIndexOffUiAsync(_lineIndex, EffectiveEncoding, ct);
            return _lineIndex.LineCount;
        }
        finally
        {
            _lineIndexLock.Release();
        }
    }

    internal async Task ResetLineIndexAsync()
    {
        await _lineIndexLock.WaitAsync();
        try
        {
            _lineIndex?.Dispose();
            _lineIndex = null;
            SearchContentVersion++;
        }
        finally
        {
            _lineIndexLock.Release();
        }
    }

    internal void ResetFilterForRotation()
    {
        _filterSession.ResetForRotation();
        RaiseFilterPropertiesChanged();
    }

    private Task<LineIndex> UpdateIndexOffUiAsync(
        LineIndex lineIndex,
        FileEncoding encoding,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.UpdateIndexAsync(FilePath, lineIndex, encoding, ct).ConfigureAwait(false), ct);

    private void RaiseFilterPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FilteredLineCount));
        OnPropertyChanged(nameof(DisplayLineCount));
        RaiseScrollMetricsChanged();
    }

    private void RaiseScrollMetricsChanged()
    {
        OnPropertyChanged(nameof(MaxScrollPosition));
        RaiseScrollBarPropertiesChanged();
    }

    private void RaiseScrollBarPropertiesChanged()
    {
        OnPropertyChanged(nameof(ScrollBarValue));
        OnPropertyChanged(nameof(ScrollBarMaximum));
        OnPropertyChanged(nameof(ScrollBarViewportSize));
    }

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        _loadCts?.Cancel();
        _navCts?.Cancel();
        _tailCoordinator.BeginShutdown();
        IsLoading = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        BeginShutdown();
        _tailCoordinator.Dispose();
        _loadCts?.Dispose();
        _navCts?.Dispose();
        _ = EnsureLineIndexDisposedTask();
    }

    private Task EnsureLineIndexDisposedTask()
    {
        var existingTask = Volatile.Read(ref _lineIndexDisposeTask);
        if (existingTask != null)
            return existingTask;

        var createdTask = DisposeLineIndexAsync();
        var publishedTask = Interlocked.CompareExchange(ref _lineIndexDisposeTask, createdTask, null) ?? createdTask;
        if (ReferenceEquals(publishedTask, createdTask))
            ObserveBackgroundTask(createdTask);

        return publishedTask;
    }

    private void DisposeLineIndexUnsafe()
    {
        _lineIndex?.Dispose();
        _lineIndex = null;
    }

    private async Task DisposeLineIndexAsync()
    {
        var lockTaken = false;
        try
        {
            await _lineIndexLock.WaitAsync().ConfigureAwait(false);
            lockTaken = true;
            DisposeLineIndexUnsafe();
        }
        catch (ObjectDisposedException) { }
        finally
        {
            if (lockTaken)
                _lineIndexLock.Release();
        }
    }

    private static void ObserveBackgroundTask(Task task)
    {
        if (task.IsCompleted)
            return;

        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
