namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Helpers;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class LogTabViewModel : ObservableObject, IDisposable, IFileSessionClient
{
    private const int StickyScrollBarMaximum = 1000;
    private const int StickyScrollBarViewportSize = 100;
    private const int ScrollPositionViewportRefreshIntervalMs = 33;
    private const int WarmSessionResumePollingMs = 250;

    public sealed partial class EncodingOptionItem : ObservableObject
    {
        public FileEncoding Value { get; init; }

        [ObservableProperty]
        private string _label = string.Empty;
    }

    private readonly FileSessionRegistry _sessionRegistry;
    private readonly bool _ownsSessionRegistry;
    private readonly LogViewportService _viewportService;
    private readonly LogFilterSession _filterSession = new();
    private readonly SemaphoreSlim _filterMutationGate = new(1, 1);
    private readonly SynchronizationContext? _uiContext = NormalizeSynchronizationContext(SynchronizationContext.Current);
    private readonly IUiDispatcher _uiDispatcher;
    private readonly BulkObservableCollection<LogLineViewModel> _visibleLines = new();
    private readonly object _scrollPositionRefreshGate = new();
    private FileEncoding _lastResolvedAutoEncoding = FileEncoding.Utf8;
    private string _lastResolvedAutoEncodingStatusText = "Auto -> UTF-8 (fallback)";
    private AppSettings _settings;
    private int _viewportRefreshToken;
    private int? _queuedScrollPositionRefreshStartLine;
    private bool _isScrollPositionRefreshDrainActive;
    private DateTime _lastScrollPositionViewportRefreshUtc = DateTime.MinValue;
    private CancellationTokenSource? _navCts;
    private FileSessionLease _sessionLease;
    private FileSession _session;
    private long _filterMutationVersion;
    private int _isDisposed;
    private int _shutdownStarted;

    internal event EventHandler? FilterSnapshotInvalidated;

    [ObservableProperty]
    private FileEncoding _encoding;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _autoScrollEnabled = true;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private DateTime _lastVisibleAtUtc = DateTime.UtcNow;

    [ObservableProperty]
    private DateTime _lastHiddenAtUtc = DateTime.MinValue;

    [ObservableProperty]
    private int _navigateToLineNumber = -1;

    [ObservableProperty]
    private int _scrollPosition;

    [ObservableProperty]
    private double _horizontalContentMinWidth;

    private string _fileId;
    private readonly string? _scopeDashboardId;

    public LogTabViewModel(
        string fileId,
        string filePath,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        AppSettings settings,
        bool skipInitialEncodingResolution = false)
        : this(
            fileId,
            filePath,
            logReader,
            tailService,
            encodingDetectionService,
            settings,
            skipInitialEncodingResolution,
            null,
            FileEncoding.Auto,
            null,
            null,
            new LogViewportCapacity())
    {
    }

    internal LogTabViewModel(
        string fileId,
        string filePath,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        AppSettings settings,
        bool skipInitialEncodingResolution,
        FileSessionRegistry? sessionRegistry,
        FileEncoding initialEncoding,
        string? scopeDashboardId,
        IUiDispatcher? uiDispatcher = null,
        LogViewportCapacity? viewportCapacity = null)
    {
        _fileId = fileId;
        _scopeDashboardId = scopeDashboardId;
        FilePath = filePath;
        _settings = settings;
        _uiDispatcher = uiDispatcher ?? WpfUiDispatcher.Instance;
        _ownsSessionRegistry = sessionRegistry == null;
        _sessionRegistry = sessionRegistry ?? new FileSessionRegistry(logReader, tailService, encodingDetectionService, _uiDispatcher);
        _encoding = initialEncoding;
        _viewportService = new LogViewportService(this, _filterSession, viewportCapacity ?? new LogViewportCapacity());
        AutoEncodingOption = new EncodingOptionItem { Value = FileEncoding.Auto, Label = "Auto (UTF-8)" };
        EncodingOptions = new[]
        {
            AutoEncodingOption,
            new EncodingOptionItem { Value = FileEncoding.Utf8, Label = "UTF-8" },
            new EncodingOptionItem { Value = FileEncoding.Utf16, Label = "UTF-16" },
            new EncodingOptionItem { Value = FileEncoding.Utf16Be, Label = "UTF-16 BE" },
            new EncodingOptionItem { Value = FileEncoding.Ansi, Label = "ANSI" }
        };

        _sessionLease = _sessionRegistry.Acquire(FilePath, initialEncoding);
        _session = _sessionLease.Session;
        AttachToSession(_session, skipInitialEncodingResolution, raiseSessionSnapshot: true);
    }

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;

    internal bool IsShutdownOrDisposed => IsShuttingDown || Volatile.Read(ref _isDisposed) != 0 || _session.IsShutdownOrDisposed;

    internal AppSettings CurrentSettings => _settings;

    internal string? ActiveFilterStatusText => _filterSession.ActiveFilterStatusText;

    internal bool HasNoLineIndex => _session.HasNoLineIndex;

    internal FileSession ActiveSession => _session;

    bool IFileSessionClient.IsSessionClientDisposed => IsShutdownOrDisposed;

    bool IFileSessionClient.IsSessionClientVisible => IsVisible;

    public string TabInstanceId { get; } = Guid.NewGuid().ToString("N");

    public string FileId
    {
        get => _fileId;
        private set => SetProperty(ref _fileId, value);
    }

    public string FilePath { get; }

    public string? ScopeDashboardId => _scopeDashboardId;

    public bool IsAdHocScope => string.IsNullOrEmpty(ScopeDashboardId);

    public string FileName => Path.GetFileName(FilePath);

    public FileEncoding EffectiveEncoding => _session.EffectiveEncoding;

    public string EncodingStatusText => _session.EncodingStatusText;

    public int TotalLines
    {
        get => _session.TotalLines;
        set => _session.SetTotalLinesForTesting(value);
    }

    public long? FileSizeBytes => _session.FileSizeBytes;

    public DateTime? LastModifiedLocal => _session.LastModifiedLocal;

    public bool IsLoading => _session.IsLoading;

    public bool HasLoadError => _session.HasLoadError;

    public bool IsSuspended => _session.IsSuspended;

    public ObservableCollection<LogLineViewModel> VisibleLines => _visibleLines;

    private EncodingOptionItem AutoEncodingOption { get; }

    public IReadOnlyList<EncodingOptionItem> EncodingOptions { get; }

    public string SelectedEncodingDisplayLabel => Encoding == FileEncoding.Auto
        ? $"Auto ({EncodingHelper.GetEncodingDisplayName(_lastResolvedAutoEncoding)})"
        : EncodingHelper.GetEncodingDisplayName(Encoding);

    public int ViewportLineCount => _viewportService.ViewportLineCount;

    internal int ViewportStartLine => _viewportService.ViewportStartLine;

    internal int ViewportRefreshToken
    {
        get => _viewportRefreshToken;
        private set => SetProperty(ref _viewportRefreshToken, value);
    }

    public bool IsFilterActive => _filterSession.IsActive;

    public int FilteredLineCount => _filterSession.FilteredLineCount;

    public int DisplayLineCount => IsFilterActive ? _filterSession.DisplayLineCount : TotalLines;

    public int MaxScrollPosition => Math.Max(0, DisplayLineCount - _viewportService.ViewportLineCount);

    public int ScrollBarValue => AutoScrollEnabled ? StickyScrollBarMaximum : ScrollPosition;

    public int ScrollBarMaximum => AutoScrollEnabled ? StickyScrollBarMaximum : MaxScrollPosition;

    public int ScrollBarViewportSize => AutoScrollEnabled ? StickyScrollBarViewportSize : ViewportLineCount;

    internal int SearchContentVersion => _session.SearchContentVersion;

    internal FileGenerationToken CurrentGenerationToken => _session.CurrentGenerationToken;

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    internal void UpdateFileId(string fileId) => FileId = fileId;

    internal void UpdateViewportLineCount(int count)
        => _viewportService.UpdateViewportLineCount(count);

    internal Task<bool> SynchronizeViewportCapacityAsync()
        => _viewportService.SynchronizeViewportCapacityAsync();

    internal void GrowHorizontalContentMinWidth(double observedWidth)
    {
        if (double.IsNaN(observedWidth) ||
            double.IsInfinity(observedWidth) ||
            observedWidth <= HorizontalContentMinWidth)
        {
            return;
        }

        HorizontalContentMinWidth = observedWidth;
    }

    internal void ResetHorizontalContentMinWidth()
        => HorizontalContentMinWidth = 0;

    public Task<bool> RefreshViewportAsync()
        => _viewportService.RefreshViewportAsync();

    public async Task LoadAsync()
    {
        if (IsShutdownOrDisposed)
            return;

        ResetHorizontalContentMinWidth();
        var session = _session;
        var canReuseWarmSession = !HasLoadError && !IsLoading && !HasNoLineIndex;
        if (canReuseWarmSession)
        {
            await session.ResumeTailingWithCatchUpAsync(WarmSessionResumePollingMs);
        }
        else
        {
            await SetStatusTextAsync(Encoding == FileEncoding.Auto ? "Detecting encoding..." : "Building index...").ConfigureAwait(false);
            await session.LoadAsync();
        }

        if (IsShutdownOrDisposed || !ReferenceEquals(session, _session))
            return;

        if (HasLoadError)
        {
            if (!string.IsNullOrWhiteSpace(_session.LastErrorMessage))
                await SetStatusTextAsync($"Error: {_session.LastErrorMessage}").ConfigureAwait(false);
            return;
        }

        var initialStart = IsFilterActive
            ? 0
            : Math.Max(0, TotalLines - _viewportService.ViewportLineCount);
        await LoadViewportAsync(initialStart, _viewportService.ViewportLineCount).ConfigureAwait(false);
        await SetStatusTextAsync(
            IsFilterActive
                ? _filterSession.ActiveFilterStatusText ?? BuildActiveFilterFallbackStatusText()
                : $"{TotalLines:N0} lines").ConfigureAwait(false);
    }

    internal RecentTabState CaptureRecentState()
    {
        return new RecentTabState
        {
            RequestedEncoding = Encoding,
            IsPinned = IsPinned,
            ViewportStartLine = ViewportStartLine,
            NavigateToLineNumber = NavigateToLineNumber,
            FilterSnapshot = _filterSession.CaptureSnapshot()
        };
    }

    internal LogFilterSession.FilterSnapshot? CaptureActiveFilterSnapshot()
        => _filterSession.CaptureSnapshot();

    internal async Task RestoreRecentStateAsync(RecentTabState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.FilterSnapshot != null)
            await TryCommitFilterSnapshotAsync(state.FilterSnapshot);

        var restoreViewportStart = AutoScrollEnabled
            ? Math.Max(0, DisplayLineCount - _viewportService.ViewportLineCount)
            : state.ViewportStartLine;
        await LoadViewportAsync(restoreViewportStart, _viewportService.ViewportLineCount);
        SetNavigateTargetLine(state.NavigateToLineNumber);
        StatusText = IsFilterActive
            ? _filterSession.ActiveFilterStatusText ?? BuildActiveFilterFallbackStatusText()
            : $"{TotalLines:N0} lines";
    }

    public Task<bool> LoadViewportAsync(int startLine, int count, CancellationToken ct = default)
        => _viewportService.LoadViewportAsync(startLine, count, ct);

    internal Task<bool> TryAppendTailLinesToViewportAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
        => _viewportService.TryAppendTailLinesToViewportAsync(previousTotalLines, updatedLineCount, ct);

    partial void OnAutoScrollEnabledChanged(bool value)
    {
        if (value)
            CancelQueuedScrollPositionRefresh();

        RaiseScrollBarPropertiesChanged();
    }

    internal void RaiseViewportPropertiesChanged()
    {
        OnPropertyChanged(nameof(ViewportLineCount));
        RaiseScrollMetricsChanged();
    }

    internal void RequestViewportRefresh()
        => ViewportRefreshToken++;

    partial void OnScrollPositionChanged(int value)
    {
        OnPropertyChanged(nameof(ScrollBarValue));
        if (_viewportService.IsSuppressingScrollChange || IsShutdownOrDisposed)
            return;

        QueueScrollPositionRefresh(value);
    }

    private Task ScrollToLineAsync(int startLine)
        => _viewportService.ScrollToLineAsync(startLine, _navCts);

    private void QueueScrollPositionRefresh(int startLine)
    {
        if (IsShutdownOrDisposed || AutoScrollEnabled)
            return;

        _navCts?.Cancel();

        var shouldStartDrain = false;
        lock (_scrollPositionRefreshGate)
        {
            _queuedScrollPositionRefreshStartLine = startLine;
            if (!_isScrollPositionRefreshDrainActive)
            {
                _isScrollPositionRefreshDrainActive = true;
                shouldStartDrain = true;
            }
        }

        if (shouldStartDrain)
            ObserveBackgroundTask(DrainQueuedScrollPositionRefreshesAsync());
    }

    private void CancelQueuedScrollPositionRefresh()
    {
        _navCts?.Cancel();
        lock (_scrollPositionRefreshGate)
            _queuedScrollPositionRefreshStartLine = null;
    }

    private async Task DrainQueuedScrollPositionRefreshesAsync()
    {
        while (true)
        {
            int startLine;
            lock (_scrollPositionRefreshGate)
            {
                if (_queuedScrollPositionRefreshStartLine == null || IsShutdownOrDisposed || AutoScrollEnabled)
                {
                    _isScrollPositionRefreshDrainActive = false;
                    return;
                }

                startLine = _queuedScrollPositionRefreshStartLine.Value;
                _queuedScrollPositionRefreshStartLine = null;
            }

            var elapsedSinceLastRefresh = DateTime.UtcNow - _lastScrollPositionViewportRefreshUtc;
            var refreshDelay = TimeSpan.FromMilliseconds(ScrollPositionViewportRefreshIntervalMs) - elapsedSinceLastRefresh;
            if (refreshDelay > TimeSpan.Zero)
                await Task.Delay(refreshDelay).ConfigureAwait(false);

            lock (_scrollPositionRefreshGate)
            {
                if (_queuedScrollPositionRefreshStartLine != null)
                {
                    startLine = _queuedScrollPositionRefreshStartLine.Value;
                    _queuedScrollPositionRefreshStartLine = null;
                }
            }

            if (IsShutdownOrDisposed || AutoScrollEnabled)
                continue;

            await InvokeOnUiAsync(() => ScrollToLineAsync(startLine)).ConfigureAwait(false);
            _lastScrollPositionViewportRefreshUtc = DateTime.UtcNow;
        }
    }

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

    public Task NavigateToLineAsync(int lineNumber)
        => _viewportService.NavigateToLineAsync(lineNumber, _navCts);

    partial void OnEncodingChanged(FileEncoding value)
    {
        if (IsShutdownOrDisposed)
            return;

        ObserveBackgroundTask(ApplyEncodingChangeAsync(value));
    }

    private async Task ApplyEncodingChangeAsync(FileEncoding value)
    {
        var clearedFilter = false;
        var shouldReload = false;
        await _filterMutationGate.WaitAsync();
        try
        {
            if (IsShutdownOrDisposed || Encoding != value)
                return;

            if (IsFilterActive)
            {
                _filterSession.Clear();
                Interlocked.Increment(ref _filterMutationVersion);
                clearedFilter = true;
            }

            shouldReload = !_session.HasNoLineIndex || _session.IsLoading;
            var skipInitialEncodingResolution = shouldReload && value == FileEncoding.Auto;
            EncodingHelper.EncodingDecision? pendingAutoEncodingDecision = skipInitialEncodingResolution
                ? new EncodingHelper.EncodingDecision(
                    FileEncoding.Auto,
                    _lastResolvedAutoEncoding,
                    _lastResolvedAutoEncodingStatusText)
                : null;
            RebindSession(value, skipInitialEncodingResolution, raiseSessionSnapshot: !shouldReload, pendingAutoEncodingDecision);
        }
        finally
        {
            _filterMutationGate.Release();
        }

        if (clearedFilter)
        {
            ResetHorizontalContentMinWidth();
            RaiseFilterPropertiesChanged();
            FilterSnapshotInvalidated?.Invoke(this, EventArgs.Empty);
        }

        OnPropertyChanged(nameof(EffectiveEncoding));
        OnPropertyChanged(nameof(SearchContentVersion));
        OnPropertyChanged(nameof(CurrentGenerationToken));
        OnPropertyChanged(nameof(SelectedEncodingDisplayLabel));

        if (!shouldReload)
            return;

        await ReloadSessionAfterEncodingChangeAsync();
    }

    public void OnBecameVisible()
    {
        if (IsShutdownOrDisposed)
            return;

        IsVisible = true;
        LastVisibleAtUtc = DateTime.UtcNow;
        _session.ResumeTailing();
    }

    public void OnBecameHidden()
    {
        if (IsShutdownOrDisposed)
            return;

        if (!IsVisible)
            return;

        IsVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
        _session.SuspendTailingIfNoVisibleClients();
    }

    public void SuspendTailing()
    {
        if (IsVisible)
            _session.SuspendTailing();
        else
            _session.SuspendTailingIfNoVisibleClients();
    }

    public void ResumeTailing()
        => _session.ResumeTailing();

    public void ApplyVisibleTailingMode(int pollingIntervalMs)
        => _session.ApplyVisibleTailingMode(pollingIntervalMs);

    public async Task ApplyFilterAsync(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest? filterRequest = null,
        bool hasParseableTimestamps = false,
        FilterLineSetMode lineSetMode = FilterLineSetMode.IncludeMatching)
    {
        ResetHorizontalContentMinWidth();
        await _filterMutationGate.WaitAsync();
        try
        {
            _filterSession.ApplyFilter(
                matchingLineNumbers,
                statusText,
                filterRequest,
                hasParseableTimestamps,
                TotalLines,
                lineSetMode);
            Interlocked.Increment(ref _filterMutationVersion);
        }
        finally
        {
            _filterMutationGate.Release();
        }

        RaiseFilterPropertiesChanged();
        await RefreshCommittedFilterAsync();
    }

    internal async Task<bool> TryCommitFilterSnapshotAsync(
        LogFilterSession.FilterSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var committed = false;
        var mutated = false;
        ExceptionDispatchInfo? commitError = null;
        await _filterMutationGate.WaitAsync(ct);
        try
        {
            var evaluatedThroughLine = Math.Max(
                0,
                snapshot.LastEvaluatedLine ??
                (snapshot.TotalLinesAtSnapshot > 0 ? snapshot.TotalLinesAtSnapshot : TotalLines));
            if (evaluatedThroughLine > TotalLines)
                await _session.UpdateLineIndexAsync(ct);

            var hasOutOfBoundaryMatch = snapshot.MatchingLineNumbers.Any(line => line > evaluatedThroughLine);
            if (!hasOutOfBoundaryMatch &&
                evaluatedThroughLine <= TotalLines &&
                IsFilterSnapshotCompatible(snapshot))
            {
                var priorSnapshot = _filterSession.CaptureSnapshot();
                try
                {
                    ResetHorizontalContentMinWidth();
                    _filterSession.RestoreSnapshot(LogFilterSession.CloneSnapshot(snapshot), TotalLines);
                    mutated = true;

                    var catchUpLineCount = TotalLines;
                    if (catchUpLineCount > evaluatedThroughLine)
                    {
                        var filterUpdate = await ProcessFilterThroughLineAsync(catchUpLineCount, ct);
                        if (filterUpdate == null)
                            throw new IOException("The filter could not catch up to the current file contents.");
                        if (filterUpdate.EvaluatedThroughLine < catchUpLineCount &&
                            !filterUpdate.IsEvaluationPaused)
                            throw new IOException("The filter could not read all appended file contents.");
                    }

                    if (IsFilterSnapshotCompatible(snapshot))
                    {
                        committed = true;
                    }
                    else
                    {
                        RestorePriorFilterAfterRejectedCommit(priorSnapshot);
                    }
                }
                catch (Exception ex)
                {
                    RestorePriorFilterAfterRejectedCommit(priorSnapshot);
                    commitError = ExceptionDispatchInfo.Capture(ex);
                }
            }
        }
        finally
        {
            if (mutated)
            {
                Interlocked.Increment(ref _filterMutationVersion);
            }

            _filterMutationGate.Release();
        }

        if (mutated)
            RaiseFilterPropertiesChanged();

        commitError?.Throw();

        return committed;
    }

    internal bool IsGenerationCompatible(FileScanGenerationEvidence generationEvidence)
    {
        if (generationEvidence.Correlation == FileGenerationCorrelation.Stale)
            return false;

        return !generationEvidence.Token.IsKnown ||
               !CurrentGenerationToken.IsKnown ||
               generationEvidence.Token == CurrentGenerationToken;
    }

    internal bool IsFilterSnapshotCompatible(LogFilterSession.FilterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!IsGenerationCompatible(snapshot.GenerationEvidence) ||
            snapshot.EvaluatedEncoding != FileEncoding.Auto &&
            snapshot.EvaluatedEncoding != EffectiveEncoding)
        {
            return false;
        }

        return !string.Equals(snapshot.CorrelatedTabInstanceId, TabInstanceId, StringComparison.Ordinal) ||
               snapshot.CorrelatedSearchContentVersion == SearchContentVersion;
    }

    internal async Task<bool> RestoreFilterSnapshotAsync(LogFilterSession.FilterSnapshot snapshot, CancellationToken ct = default)
    {
        if (!await TryCommitFilterSnapshotAsync(snapshot, ct))
            return false;

        await RefreshCommittedFilterAsync(ct);
        return true;
    }

    internal async Task RefreshCommittedFilterAsync(CancellationToken ct = default)
    {
        if (!IsFilterActive)
            return;

        ResetHorizontalContentMinWidth();

        var filterViewportStartLine = AutoScrollEnabled
            ? Math.Max(0, DisplayLineCount - _viewportService.ViewportLineCount)
            : 0;
        var viewportApplied = await LoadViewportAsync(filterViewportStartLine, _viewportService.ViewportLineCount, ct);
        if (viewportApplied)
            SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? -1);

        StatusText = IsFilterActive
            ? _filterSession.ActiveFilterStatusText ?? BuildActiveFilterFallbackStatusText()
            : $"{TotalLines:N0} lines";
    }

    private void RestorePriorFilterAfterRejectedCommit(LogFilterSession.FilterSnapshot? priorSnapshot)
    {
        if (priorSnapshot != null && IsFilterSnapshotCompatible(priorSnapshot))
            _filterSession.RestoreSnapshot(priorSnapshot, TotalLines);
        else
            _filterSession.Clear();
    }

    public Task ClearFilterAsync()
        => ClearFilterCoreAsync(requireIncompatibleSnapshot: false);

    private async Task ClearFilterCoreAsync(bool requireIncompatibleSnapshot)
    {
        var cleared = false;
        long publicationVersion = 0;
        await _filterMutationGate.WaitAsync();
        try
        {
            if (IsFilterActive)
            {
                if (requireIncompatibleSnapshot)
                {
                    var activeSnapshot = _filterSession.CaptureSnapshot();
                    if (activeSnapshot == null || IsFilterSnapshotCompatible(activeSnapshot))
                        return;
                }

                _filterSession.Clear();
                publicationVersion = Interlocked.Increment(ref _filterMutationVersion);
                cleared = true;
            }
        }
        finally
        {
            _filterMutationGate.Release();
        }

        if (!cleared)
            return;

        await InvokeOnUiAsync(async () =>
        {
            if (IsShutdownOrDisposed || Volatile.Read(ref _filterMutationVersion) != publicationVersion)
                return;

            ResetHorizontalContentMinWidth();
            RaiseFilterPropertiesChanged();
            if (requireIncompatibleSnapshot)
                FilterSnapshotInvalidated?.Invoke(this, EventArgs.Empty);

            var viewportApplied = await LoadViewportAsync(
                Math.Max(0, TotalLines - _viewportService.ViewportLineCount),
                _viewportService.ViewportLineCount);
            if (IsShutdownOrDisposed || Volatile.Read(ref _filterMutationVersion) != publicationVersion)
                return;

            if (viewportApplied)
                SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? (TotalLines > 0 ? 1 : -1));

            StatusText = $"{TotalLines:N0} lines";
        }).ConfigureAwait(false);
    }

    private void ScheduleFilterCompatibilityCheck()
    {
        if (IsFilterActive && !IsShutdownOrDisposed)
            ObserveBackgroundTask(ClearFilterCoreAsync(requireIncompatibleSnapshot: true));
    }

    internal async Task ApplyTailFilterForAppendedLinesAsync(int updatedLineCount, CancellationToken ct)
    {
        LogFilterSession.FilterTailUpdateResult? filterUpdate;
        long publicationVersion;
        await _filterMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsFilterActive)
                return;

            filterUpdate = await ProcessFilterThroughLineAsync(updatedLineCount, ct).ConfigureAwait(false);
            publicationVersion = Interlocked.Increment(ref _filterMutationVersion);
        }
        finally
        {
            _filterMutationGate.Release();
        }

        if (filterUpdate == null || IsShutdownOrDisposed)
            return;

        await InvokeOnUiAsync(async () =>
        {
            if (IsShutdownOrDisposed || Volatile.Read(ref _filterMutationVersion) != publicationVersion)
                return;

            StatusText = filterUpdate.StatusText;
            if (!filterUpdate.HasChanges)
                return;

            RaiseFilterPropertiesChanged();
            if (!AutoScrollEnabled)
                return;

            if (filterUpdate.HasCompleteAddedMatchingLines)
            {
                var updatedInPlace = TryAppendFilteredTailLinesToViewportInPlace(
                    filterUpdate.PreviousDisplayCount,
                    filterUpdate.AddedMatchingLines);
                if (updatedInPlace)
                    return;
            }

            _ = await LoadViewportAsync(
                Math.Max(0, DisplayLineCount - _viewportService.ViewportLineCount),
                _viewportService.ViewportLineCount,
                ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private Task<LogFilterSession.FilterTailUpdateResult?> ProcessFilterThroughLineAsync(
        int updatedLineCount,
        CancellationToken ct)
        => _session.WithLineIndexLeaseAsync(
            async (lineIndex, effectiveEncoding, innerCt) =>
                await _filterSession.ProcessAppendedLinesAsync(
                    updatedLineCount,
                    lineIndex,
                    effectiveEncoding,
                    _session.ReadLinesOffUiAsync,
                    _viewportService.ViewportLineCount,
                    innerCt).ConfigureAwait(false),
            ct);

    private bool TryAppendFilteredTailLinesToViewportInPlace(
        int previousDisplayCount,
        IReadOnlyList<LogFilterSession.FilterTailMatch> addedMatchingLines)
        => _viewportService.TryAppendFilteredTailLinesToViewportInPlace(previousDisplayCount, addedMatchingLines);

    public Task ResumeTailingWithCatchUpAsync(int pollingIntervalMs)
        => _session.ResumeTailingWithCatchUpAsync(pollingIntervalMs);

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

    internal int? GetAdjacentDisplayLineNumber(int lineNumber, int direction)
    {
        if (direction == 0)
            return lineNumber;

        var currentDisplayIndex = GetDisplayIndexForLineNumber(lineNumber);
        if (currentDisplayIndex == null)
            return null;

        return GetDisplayLineNumberAt(currentDisplayIndex.Value + Math.Sign(direction));
    }

    internal void ApplyVisibleLines(IReadOnlyList<LogLineViewModel> nextVisibleLines)
        => _visibleLines.ReplaceAll(nextVisibleLines);

    internal Task<IReadOnlyList<string>> ReadLinesOffUiAsync(
        LineIndex lineIndex,
        int startLine,
        int count,
        FileEncoding encoding,
        CancellationToken ct)
        => _session.ReadLinesOffUiAsync(lineIndex, startLine, count, encoding, ct);

    internal Task<string> ReadLineOffUiAsync(
        LineIndex lineIndex,
        int lineNumber,
        FileEncoding encoding,
        CancellationToken ct)
        => _session.ReadLineOffUiAsync(lineIndex, lineNumber, encoding, ct);

    internal Task<TResult?> WithLineIndexLeaseAsync<TResult>(
        Func<LineIndex, FileEncoding, CancellationToken, Task<TResult>> action,
        CancellationToken ct = default)
        => _session.WithLineIndexLeaseAsync(action, ct);

    internal Task<bool> WithLineIndexLeaseAsync(
        Func<LineIndex, FileEncoding, CancellationToken, Task> action,
        CancellationToken ct = default)
        => _session.WithLineIndexLeaseAsync(action, ct);

    internal void ReplaceNavigationCts(CancellationTokenSource cts)
    {
        _navCts = cts;
    }

    internal Task<int?> UpdateLineIndexLineCountAsync(CancellationToken ct)
        => _session.UpdateLineIndexLineCountAsync(ct);

    internal Task ResetLineIndexAsync()
        => _session.ResetLineIndexAsync();

    internal async Task ResetFilterForRotationAsync(CancellationToken ct)
    {
        await _filterMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _filterSession.ResetForRotation();
            Interlocked.Increment(ref _filterMutationVersion);
        }
        finally
        {
            _filterMutationGate.Release();
        }

        await InvokeOnUiAsync(() =>
        {
            ResetHorizontalContentMinWidth();
            RaiseFilterPropertiesChanged();
            FilterSnapshotInvalidated?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(false);
    }

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        _navCts?.Cancel();
        DetachFromSession(_session);
    }

    void IFileSessionClient.SetStatusText(string statusText)
    {
        if (IsShutdownOrDisposed)
            return;

        SetStatusTextFromAnyThread(statusText);
    }

    async Task IFileSessionClient.HandleSessionContentAdvancedAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
    {
        if (IsShutdownOrDisposed)
            return;

        if (IsFilterActive)
        {
            await ApplyTailFilterForAppendedLinesAsync(updatedLineCount, ct).ConfigureAwait(false);
            return;
        }

        var filterMutationVersion = Volatile.Read(ref _filterMutationVersion);
        if (AutoScrollEnabled)
        {
            var updatedInPlace = await TryAppendTailLinesToViewportAsync(previousTotalLines, updatedLineCount, ct).ConfigureAwait(false);
            if (!updatedInPlace)
                await LoadViewportAsync(Math.Max(0, TotalLines - ViewportLineCount), ViewportLineCount, ct).ConfigureAwait(false);
        }

        await SetUnfilteredStatusTextAsync($"{TotalLines:N0} lines", filterMutationVersion).ConfigureAwait(false);
    }

    async Task IFileSessionClient.HandleSessionReloadedAsync(CancellationToken ct)
    {
        if (IsShutdownOrDisposed)
            return;

        await InvokeOnUiAsync(ResetHorizontalContentMinWidth).ConfigureAwait(false);
        if (IsFilterActive)
            await ResetFilterForRotationAsync(ct).ConfigureAwait(false);

        var filterMutationVersion = Volatile.Read(ref _filterMutationVersion);
        await LoadViewportAsync(Math.Max(0, TotalLines - ViewportLineCount), ViewportLineCount, ct).ConfigureAwait(false);
        await SetUnfilteredStatusTextAsync($"{TotalLines:N0} lines", filterMutationVersion).ConfigureAwait(false);
        await InvokeOnUiAsync(RequestViewportRefresh).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        BeginShutdown();
        _viewportService.Dispose();
        _navCts?.Dispose();
        DetachFromSession(_session);
        _sessionLease.Dispose();
        if (_ownsSessionRegistry)
            _sessionRegistry.Dispose();
    }

    private void AttachToSession(FileSession session, bool skipInitialEncodingResolution, bool raiseSessionSnapshot)
    {
        session.AttachClient(this);
        session.PropertyChanged += Session_PropertyChanged;
        if (!skipInitialEncodingResolution)
            session.EnsureInitialEncodingResolved();

        CaptureResolvedAutoEncoding(session);
        UpdateAutoEncodingLabel();
        if (raiseSessionSnapshot)
            RaiseSessionBackedPropertyChanges();
    }

    private void DetachFromSession(FileSession session)
    {
        session.PropertyChanged -= Session_PropertyChanged;
        session.DetachClient(this);
    }

    private void RebindSession(
        FileEncoding requestedEncoding,
        bool skipInitialEncodingResolution,
        bool raiseSessionSnapshot,
        EncodingHelper.EncodingDecision? pendingAutoEncodingDecision = null)
    {
        var previousLease = _sessionLease;
        var previousSession = _session;

        DetachFromSession(previousSession);

        _sessionLease = _sessionRegistry.Acquire(FilePath, requestedEncoding);
        _session = _sessionLease.Session;
        if (pendingAutoEncodingDecision is { } pendingDecision)
            _session.SeedPendingEncodingDisplay(pendingDecision);

        AttachToSession(_session, skipInitialEncodingResolution, raiseSessionSnapshot);

        previousLease.Dispose();
    }

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _session))
            return;

        switch (e.PropertyName)
        {
            case nameof(FileSession.EffectiveEncoding):
                CaptureResolvedAutoEncoding(_session);
                UpdateAutoEncodingLabel();
                OnPropertyChanged(nameof(EffectiveEncoding));
                OnPropertyChanged(nameof(SelectedEncodingDisplayLabel));
                ScheduleFilterCompatibilityCheck();
                break;
            case nameof(FileSession.EncodingStatusText):
                CaptureResolvedAutoEncoding(_session);
                UpdateAutoEncodingLabel();
                OnPropertyChanged(nameof(SelectedEncodingDisplayLabel));
                OnPropertyChanged(nameof(EncodingStatusText));
                break;
            case nameof(FileSession.TotalLines):
                OnPropertyChanged(nameof(TotalLines));
                OnPropertyChanged(nameof(DisplayLineCount));
                RaiseScrollMetricsChanged();
                break;
            case nameof(FileSession.FileSizeBytes):
                OnPropertyChanged(nameof(FileSizeBytes));
                break;
            case nameof(FileSession.LastModifiedLocal):
                OnPropertyChanged(nameof(LastModifiedLocal));
                break;
            case nameof(FileSession.IsLoading):
                OnPropertyChanged(nameof(IsLoading));
                break;
            case nameof(FileSession.HasLoadError):
                if (_session.HasLoadError && !string.IsNullOrWhiteSpace(_session.LastErrorMessage))
                    SetStatusTextFromAnyThread($"Error: {_session.LastErrorMessage}");
                OnPropertyChanged(nameof(HasLoadError));
                break;
            case nameof(FileSession.LastErrorMessage):
                if (_session.HasLoadError && !string.IsNullOrWhiteSpace(_session.LastErrorMessage))
                    SetStatusTextFromAnyThread($"Error: {_session.LastErrorMessage}");
                break;
            case nameof(FileSession.IsSuspended):
                OnPropertyChanged(nameof(IsSuspended));
                break;
            case nameof(FileSession.SearchContentVersion):
                OnPropertyChanged(nameof(SearchContentVersion));
                ScheduleFilterCompatibilityCheck();
                break;
            case nameof(FileSession.CurrentGenerationToken):
                OnPropertyChanged(nameof(CurrentGenerationToken));
                ScheduleFilterCompatibilityCheck();
                break;
        }
    }

    private void RaiseSessionBackedPropertyChanges()
    {
        OnPropertyChanged(nameof(EffectiveEncoding));
        OnPropertyChanged(nameof(EncodingStatusText));
        OnPropertyChanged(nameof(TotalLines));
        OnPropertyChanged(nameof(FileSizeBytes));
        OnPropertyChanged(nameof(LastModifiedLocal));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(IsSuspended));
        OnPropertyChanged(nameof(SearchContentVersion));
        OnPropertyChanged(nameof(CurrentGenerationToken));
        OnPropertyChanged(nameof(SelectedEncodingDisplayLabel));
        OnPropertyChanged(nameof(DisplayLineCount));
        RaiseScrollMetricsChanged();
    }

    private void UpdateAutoEncodingLabel()
    {
        AutoEncodingOption.Label = $"Auto ({EncodingHelper.GetEncodingDisplayName(_lastResolvedAutoEncoding)})";
    }

    private void CaptureResolvedAutoEncoding(FileSession session)
    {
        if (session.RequestedEncoding != FileEncoding.Auto)
            return;

        _lastResolvedAutoEncoding = session.EffectiveEncoding;
        _lastResolvedAutoEncodingStatusText = session.EncodingStatusText;
    }

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

    private int? GetDisplayIndexForLineNumber(int lineNumber)
    {
        if (lineNumber <= 0)
            return null;

        if (!IsFilterActive)
            return lineNumber <= TotalLines ? lineNumber - 1 : null;

        return _filterSession.GetDisplayIndexForLineNumber(lineNumber);
    }

    private int? GetDisplayLineNumberAt(int displayIndex)
    {
        if (displayIndex < 0 || displayIndex >= DisplayLineCount)
            return null;

        if (!IsFilterActive)
            return displayIndex + 1;

        return _filterSession.GetDisplayLineNumberAt(displayIndex);
    }

    private string BuildActiveFilterFallbackStatusText()
        => $"Filter active: {DisplayLineCount:N0} matching lines.";

    private Task SetStatusTextAsync(string statusText)
        => InvokeOnUiAsync(() => StatusText = statusText);

    private Task SetUnfilteredStatusTextAsync(string statusText, long expectedFilterMutationVersion)
        => InvokeOnUiAsync(() =>
        {
            if (!IsFilterActive && Volatile.Read(ref _filterMutationVersion) == expectedFilterMutationVersion)
                StatusText = statusText;
        });

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

    private void SetStatusTextFromAnyThread(string statusText)
    {
        if (_uiContext == null || SynchronizationContext.Current == _uiContext)
        {
            StatusText = statusText;
            return;
        }

        _ = InvokeOnUiAsync(() => StatusText = statusText);
    }

    private async Task ReloadSessionAfterEncodingChangeAsync()
    {
        try
        {
            await LoadAsync().ConfigureAwait(false);
        }
        finally
        {
            await InvokeOnUiAsync(() => OnPropertyChanged(nameof(IsLoading))).ConfigureAwait(false);
        }
    }

    internal Task InvokeOnUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_uiContext != null)
        {
            if (SynchronizationContext.Current == _uiContext)
            {
                action();
                return Task.CompletedTask;
            }

            try
            {
                _uiContext.Send(static state =>
                {
                    var callback = (Action)state!;
                    callback();
                }, action);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        return _uiDispatcher.InvokeAsync(action);
    }

    internal Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_uiContext != null)
        {
            if (SynchronizationContext.Current == _uiContext)
                return Task.FromResult(action());

            try
            {
                var result = default(T)!;
                _uiContext.Send(static state =>
                {
                    var callbackState = ((Func<T> Callback, Action<T> Publish))state!;
                    callbackState.Publish(callbackState.Callback());
                }, (action, (Action<T>)(value => result = value)));
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        if (_uiDispatcher.CheckAccess())
            return Task.FromResult(action());

        var dispatcherResult = default(T)!;
        return _uiDispatcher.InvokeAsync(() => dispatcherResult = action()).ContinueWith(
            completedTask =>
            {
                completedTask.GetAwaiter().GetResult();
                return dispatcherResult;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal Task InvokeOnUiAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_uiContext != null)
        {
            if (SynchronizationContext.Current == _uiContext)
                return action();

            try
            {
                Task? task = null;
                _uiContext.Send(static state =>
                {
                    var callbackState = ((Func<Task> Callback, Action<Task> Publish))state!;
                    callbackState.Publish(callbackState.Callback());
                }, (action, (Action<Task>)(createdTask => task = createdTask)));
                return task ?? Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        return _uiDispatcher.InvokeAsync(action);
    }

    private static SynchronizationContext? NormalizeSynchronizationContext(SynchronizationContext? context)
    {
        if (context == null)
            return null;

        var assemblyName = context.GetType().Assembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(assemblyName) &&
            assemblyName.StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return context;
    }
}
