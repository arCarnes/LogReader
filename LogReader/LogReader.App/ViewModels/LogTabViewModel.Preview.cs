namespace LogReader.App.ViewModels;

using System.IO;
using LogReader.App.Services;
using LogReader.Core.Models;

public sealed record LogPreviewLineViewModel(string Text, bool IsTruncated)
{
    internal long ByteOffset { get; init; }
    internal long Revision { get; init; }
}

public partial class LogTabViewModel
{
    private bool _previewSelected;
    private bool _interactiveDashboardContent;
    private bool _restoringDashboardContent;
    private bool _waitForRestoredFilter;
    private CancellationTokenSource? _previewCts;
    private RecentLogPreview? _preview;
    private long _previewRevision;
    private long? _previewAnchor;
    private FileGenerationToken _previewGeneration;
    private FileEncoding _previewEncoding;
    private int _previewAttempt;
    private readonly HashSet<long> _previewSelection = new();
    private string? _previewMessage;

    public IReadOnlyList<LogPreviewLineViewModel> PreviewLines { get; private set; } = Array.Empty<LogPreviewLineViewModel>();
    public bool HasPreview => _preview != null && _preview.IsAvailable && !_waitForRestoredFilter;
    public bool IsIndexReady => !HasNoLineIndex && !IsLoading && !_restoringDashboardContent && !HasLoadError;
    public bool ShowIndexedContent => !_interactiveDashboardContent || IsIndexReady;
    private bool CanNavigateIndexedContent => ShowIndexedContent;

    private void NotifyIndexNavigationChanged()
    {
        JumpToTopCommand.NotifyCanExecuteChanged();
        JumpToBottomCommand.NotifyCanExecuteChanged();
    }
    public bool IsWaitingForContent => _interactiveDashboardContent && !IsIndexReady && !HasPreview;
    public string PreviewStatusText => _waitForRestoredFilter ? "Waiting for indexed filtered content…"
        : _previewMessage ?? (HasPreview ? "Recent lines preview — indexing…" : "Preparing recent lines preview…");
    internal long? PreviewAnchor => _previewAnchor;
    internal IReadOnlySet<long> PreviewSelectedOffsets => _previewSelection;
    public string LoadingProgressText => HasLoadError ? DisplayStatusText : _restoringDashboardContent && !HasNoLineIndex && !IsLoading
        ? "Restoring content…" : _session.IndexProgress is { TotalBytes: > 0 } progress && IsLoading
            ? $"Indexing {progress.ProcessedBytes / 1048576d:N1} MB of {progress.TotalBytes / 1048576d:N1} MB"
            : _session.LoadPhase == FileLoadPhase.Resolving ? "Resolving file encoding…"
            : _session.LoadPhase == FileLoadPhase.Indexing ? "Building index…" : "Waiting to index…";
    public double LoadingProgressValue => _session.IndexProgress.TotalBytes is > 0
        ? Math.Min(100, _session.IndexProgress.ProcessedBytes * 100d / _session.IndexProgress.TotalBytes) : 0;

    internal void SetDashboardContentRestoring(bool restoring, bool waitForFilter = false)
    {
        _restoringDashboardContent = restoring;
        if (restoring) _interactiveDashboardContent = true;
        _waitForRestoredFilter = waitForFilter;
        OnPropertyChanged(nameof(IsIndexReady));
        OnPropertyChanged(nameof(ShowIndexedContent));
        OnPropertyChanged(nameof(IsWaitingForContent));
        OnPropertyChanged(nameof(LoadingProgressText));
        OnPropertyChanged(nameof(HasPreview));
        NotifyIndexNavigationChanged();
    }

    internal void SetPreviewSelected(bool selected)
    {
        _previewSelected = selected;
        _previewCts?.Cancel();
        _previewCts = null;
        if (!selected)
        {
            _preview = null;
            PreviewLines = Array.Empty<LogPreviewLineViewModel>();
            _previewRevision++;
            PublishPreviewChanged();
            return;
        }
        if (IsAdHocScope || IsIndexReady || _waitForRestoredFilter || IsShutdownOrDisposed)
            return;
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        ObserveBackgroundTask(LoadPreviewAsync(cts));
    }

    private async Task LoadPreviewAsync(CancellationTokenSource cts)
    {
        try
        {
            var preview = await _session.ReadRecentPreviewAsync(cts.Token).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                if (cts.IsCancellationRequested || !_previewSelected || IsShutdownOrDisposed || IsIndexReady || _waitForRestoredFilter)
                    return;
                if (_previewGeneration.IsKnown && (preview?.Generation != _previewGeneration || preview?.Encoding != _previewEncoding))
                {
                    _previewAnchor = null;
                    _previewSelection.Clear();
                }
                _preview = preview;
                _previewGeneration = preview?.Generation ?? FileGenerationToken.Unknown;
                _previewEncoding = preview?.Encoding ?? EffectiveEncoding;
                _previewAttempt = Math.Max(1, _session.IndexProgress.Attempt);
                _previewMessage = preview?.UnavailableReason;
                _previewRevision++;
                PreviewLines = preview?.Lines.Select(line => new LogPreviewLineViewModel(line.Text, line.IsTruncated)
                    { ByteOffset = line.ByteOffset, Revision = _previewRevision }).ToArray() ?? Array.Empty<LogPreviewLineViewModel>();
                PublishPreviewChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await InvokeOnUiAsync(() =>
            {
                if (!cts.IsCancellationRequested && !IsShutdownOrDisposed)
                {
                    _previewMessage = $"Recent lines preview unavailable: {ex.Message}";
                    PublishPreviewChanged();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await InvokeOnUiAsync(() =>
            {
                if (ReferenceEquals(_previewCts, cts)) _previewCts = null;
                cts.Dispose();
            }).ConfigureAwait(false);
        }
    }

    internal void InvalidatePreviewIfBuildChanged()
    {
        if (!_previewGeneration.IsKnown || _previewEncoding == EffectiveEncoding && _session.IndexProgress.Attempt <= _previewAttempt)
            return;
        _preview = null;
        _previewGeneration = FileGenerationToken.Unknown;
        _previewAnchor = null;
        _previewSelection.Clear();
        PreviewLines = Array.Empty<LogPreviewLineViewModel>();
        PublishPreviewChanged();
        if (_previewSelected) SetPreviewSelected(true);
    }

    internal void RememberPreviewPosition(long? anchor, IEnumerable<long> selection)
    {
        _previewAnchor = anchor;
        _previewSelection.Clear();
        _previewSelection.UnionWith(selection);
    }

    internal string GetPreviewCopyText(IEnumerable<LogPreviewLineViewModel> selected)
    {
        var rows = selected.ToArray();
        if (!HasPreview || rows.Any(row => row.Revision != _previewRevision || !PreviewLines.Contains(row)))
            throw new IOException("The recent lines preview changed before Copy completed.");
        return string.Join(Environment.NewLine, rows.OrderBy(row => row.ByteOffset)
            .Select(row => row.Text + (row.IsTruncated ? " …" : string.Empty)));
    }

    internal async Task CompletePreviewTransitionAsync()
    {
        _previewCts?.Cancel();
        if (_previewGeneration.IsKnown && _previewGeneration == CurrentGenerationToken && !IsFilterActive)
        {
            await _session.WithLineIndexLeaseAsync(async (index, _, ct) =>
            {
                int? FindLine(long offset)
                {
                    var low = 0;
                    var high = index.LineCount - 1;
                    while (low <= high)
                    {
                        var middle = low + (high - low) / 2;
                        var value = index.LineOffsets[middle];
                        if (value == offset) return middle;
                        if (value < offset) low = middle + 1; else high = middle - 1;
                    }
                    return null;
                }
                if (!AutoScrollEnabled && _previewAnchor is { } anchor && FindLine(anchor) is { } start)
                    await LoadOrDeferDashboardViewportAsync(start, ct).ConfigureAwait(false);
                var selected = _previewSelection.Select(FindLine).OfType<int>().Select(line => line + 1).ToArray();
                await InvokeOnUiAsync(() =>
                {
                    ResetSelection();
                    ApplyUserSelectionChanges(selected, Array.Empty<int>());
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        await InvokeOnUiAsync(() =>
        {
            _preview = null;
            PreviewLines = Array.Empty<LogPreviewLineViewModel>();
            _previewRevision++;
            SetDashboardContentRestoring(false);
            PublishPreviewChanged();
        }).ConfigureAwait(false);
    }

    private void PublishPreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewLines));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(PreviewStatusText));
        OnPropertyChanged(nameof(IsWaitingForContent));
        OnPropertyChanged(nameof(ShowIndexedContent));
    }

    internal async Task SetDashboardLoadFailureAsync(string message)
    {
        await _session.SetDashboardLoadFailureAsync(message);
        SetDashboardContentRestoring(false);
    }
}
