namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Helpers;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class LogTabViewModel : ObservableObject, IDisposable
{
    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly SemaphoreSlim _lineIndexLock = new(1, 1);
    private AppSettings _settings;
    private LineIndex? _lineIndex;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _navCts;
    private int _isDisposed;

    public string FileId { get; }
    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);

    [ObservableProperty]
    private FileEncoding _encoding = FileEncoding.Utf8;

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

    private int _viewportStartLine;
    private int _viewportLineCount = 50; // initial estimate; corrected by SizeChanged
    private bool _suppressScrollChange;

    public static IReadOnlyList<FileEncoding> EncodingOptions { get; } = new[]
    {
        FileEncoding.Utf8,
        FileEncoding.Utf8Bom,
        FileEncoding.Ansi,
        FileEncoding.Utf16,
        FileEncoding.Utf16Be
    };

    public int ViewportLineCount => _viewportLineCount;
    public int MaxScrollPosition => Math.Max(0, TotalLines - _viewportLineCount);

    [ObservableProperty]
    private int _scrollPosition;

    public LogTabViewModel(string fileId, string filePath, ILogReaderService logReader, IFileTailService tailService, AppSettings settings)
    {
        FileId = fileId;
        FilePath = filePath;
        _logReader = logReader;
        _tailService = tailService;
        _settings = settings;

        _tailService.LinesAppended += OnLinesAppended;
        _tailService.FileRotated += OnFileRotated;
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    internal void UpdateViewportLineCount(int count)
    {
        if (count <= 0 || _viewportLineCount == count) return;
        _viewportLineCount = count;
        _suppressScrollChange = true;
        OnPropertyChanged(nameof(ViewportLineCount));
        OnPropertyChanged(nameof(MaxScrollPosition));
        _suppressScrollChange = false;
        _ = LoadViewportAsync(_viewportStartLine, _viewportLineCount);
    }

    public Task RefreshViewportAsync() => LoadViewportAsync(_viewportStartLine, _viewportLineCount);

    public async Task LoadAsync()
    {
        // Cancel and dispose any in-flight load so we don't race.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoading = true;
        HasLoadError = false;
        StatusText = "Building index...";

        try
        {
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

            var newIndex = await _logReader.BuildIndexAsync(FilePath, Encoding, cts.Token);
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

            StatusText = $"{TotalLines:N0} lines";

            // Load initial viewport
            await LoadViewportAsync(Math.Max(0, TotalLines - _viewportLineCount), _viewportLineCount);

            // Start tailing
            _tailService.StartTailing(FilePath, Encoding);
            IsSuspended = false;
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

    public async Task LoadViewportAsync(int startLine, int count, CancellationToken ct = default)
    {
        var maxStart = Math.Max(0, TotalLines - Math.Max(1, count));
        _viewportStartLine = Math.Max(0, Math.Min(startLine, maxStart));

        try
        {
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

            if (lineIndexSnapshot == null) return;
            var lines = await _logReader.ReadLinesAsync(
                FilePath,
                lineIndexSnapshot,
                _viewportStartLine,
                count,
                Encoding,
                ct);

            VisibleLines.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                VisibleLines.Add(new LogLineViewModel
                {
                    LineNumber = _viewportStartLine + i + 1,
                    Text = lines[i],
                    HighlightColor = LineHighlighter.GetHighlightColor(_settings.HighlightRules, lines[i])
                });
            }
            _suppressScrollChange = true;
            ScrollPosition = _viewportStartLine;
            _suppressScrollChange = false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            StatusText = $"Read error: {ex.Message}";
        }
    }

    partial void OnTotalLinesChanged(int value)
    {
        OnPropertyChanged(nameof(MaxScrollPosition));
    }

    partial void OnScrollPositionChanged(int value)
    {
        if (_suppressScrollChange) return;
        AutoScrollEnabled = false;
        _ = ScrollToLineAsync(value);
    }

    private async Task ScrollToLineAsync(int startLine)
    {
        _navCts?.Cancel();
        _navCts?.Dispose();
        _navCts = new CancellationTokenSource();
        try { await LoadViewportAsync(startLine, _viewportLineCount, _navCts.Token); }
        catch (OperationCanceledException) { return; }
        NavigateToLineNumber = -1;
        NavigateToLineNumber = _viewportStartLine + 1;
    }

    [RelayCommand]
    private async Task JumpToTop()
    {
        AutoScrollEnabled = false;
        await ScrollToLineAsync(0);
        NavigateToLineNumber = -1;
        NavigateToLineNumber = 1;
    }

    [RelayCommand]
    private async Task JumpToBottom()
    {
        await ScrollToLineAsync(Math.Max(0, TotalLines - _viewportLineCount));
        NavigateToLineNumber = -1;
        NavigateToLineNumber = TotalLines;
        AutoScrollEnabled = true;
    }

    public async Task NavigateToLineAsync(int lineNumber)
    {
        // Cancel any in-progress navigation so rapid clicks don't race
        _navCts?.Cancel();
        _navCts?.Dispose();
        _navCts = new CancellationTokenSource();
        var ct = _navCts.Token;

        int startLine = Math.Max(0, lineNumber - _viewportLineCount / 2);
        try
        {
            await LoadViewportAsync(startLine, _viewportLineCount, ct);
        }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        // Reset first so repeated clicks on the same line still fire PropertyChanged
        NavigateToLineNumber = -1;
        NavigateToLineNumber = lineNumber;
    }

    partial void OnEncodingChanged(FileEncoding value)
    {
        // If the tab hasn't started loading yet, the upcoming explicit LoadAsync will use the correct encoding.
        // If a load is already in progress, LoadAsync will cancel the old one and restart.
        if (Volatile.Read(ref _lineIndex) == null && !IsLoading) return;
        _tailService.StopTailing(FilePath);
        _ = LoadAsync();
    }

    public void OnBecameVisible(bool globalAutoTailEnabled)
    {
        IsVisible = true;
        LastVisibleAtUtc = DateTime.UtcNow;
        ResumeTailingIfAllowed(globalAutoTailEnabled);
    }

    public void OnBecameHidden()
    {
        if (!IsVisible) return;
        IsVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
        SuspendTailing();
    }

    public void SuspendTailing()
    {
        if (IsSuspended) return;
        _tailService.StopTailing(FilePath);
        IsSuspended = true;
    }

    public void ResumeTailingIfAllowed(bool globalAutoTailEnabled)
    {
        if (!globalAutoTailEnabled)
        {
            SuspendTailing();
            return;
        }

        if (!IsSuspended || Volatile.Read(ref _lineIndex) == null || IsLoading) return;
        _tailService.StartTailing(FilePath, Encoding);
        IsSuspended = false;
    }

    private async void OnLinesAppended(object? sender, TailEventArgs e)
    {
        if (!string.Equals(e.FilePath, FilePath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            int? updatedLineCount = null;
            await _lineIndexLock.WaitAsync();
            try
            {
                if (_lineIndex != null)
                {
                    _lineIndex = await _logReader.UpdateIndexAsync(FilePath, _lineIndex, Encoding);
                    updatedLineCount = _lineIndex.LineCount;
                }
            }
            finally
            {
                _lineIndexLock.Release();
            }

            if (updatedLineCount == null) return;

            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null) return;

            // Keep all VM-bound updates on the UI thread.
            _ = app.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    TotalLines = updatedLineCount.Value;
                    StatusText = $"{TotalLines:N0} lines";
                    if (!AutoScrollEnabled) return;

                    await LoadViewportAsync(Math.Max(0, TotalLines - _viewportLineCount), _viewportLineCount);
                    NavigateToLineNumber = -1;
                    NavigateToLineNumber = TotalLines;
                }
                catch (OperationCanceledException) { }
            });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            StatusText = $"Tail error: {ex.Message}";
        }
    }

    private void OnFileRotated(object? sender, FileRotatedEventArgs e)
    {
        if (!string.Equals(e.FilePath, FilePath, StringComparison.OrdinalIgnoreCase)) return;

        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return;

        _ = app.Dispatcher.BeginInvoke(async () =>
        {
            StatusText = "File rotated, reloading...";
            await _lineIndexLock.WaitAsync();
            try
            {
                _lineIndex?.Dispose();
                _lineIndex = null;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            await LoadAsync();
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        _tailService.LinesAppended -= OnLinesAppended;
        _tailService.FileRotated -= OnFileRotated;
        _tailService.StopTailing(FilePath);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _navCts?.Cancel();
        _navCts?.Dispose();

        if (_lineIndexLock.Wait(0))
        {
            try
            {
                DisposeLineIndexUnsafe();
            }
            finally
            {
                _lineIndexLock.Release();
            }
        }
        else
        {
            _ = DisposeLineIndexAsync();
        }
    }

    private void DisposeLineIndexUnsafe()
    {
        _lineIndex?.Dispose();
        _lineIndex = null;
    }

    private async Task DisposeLineIndexAsync()
    {
        try
        {
            await _lineIndexLock.WaitAsync().ConfigureAwait(false);
            DisposeLineIndexUnsafe();
        }
        finally
        {
            _lineIndexLock.Release();
        }
    }
}
