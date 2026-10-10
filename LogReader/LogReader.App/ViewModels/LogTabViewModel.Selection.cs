namespace LogReader.App.ViewModels;

using System.IO;
using System.Text;
using LogReader.Core.Models;

public partial class LogTabViewModel
{
    private readonly SortedSet<int> _selectedLineNumbers = new();
    private SortedSet<int>? _extensionBase;
    private int _viewportMutationDepth;
    private int _selectionRevision;
    private long _selectionLifecycleRevision;
    private FileGenerationToken _selectionGenerationToken;

    internal IReadOnlySet<int> SelectedLineNumbers => _selectedLineNumbers;
    internal int? SelectionAnchor { get; private set; }
    internal int? SelectionCaret { get; private set; }
    internal int SelectionRevision => _selectionRevision;
    internal long SelectionLifecycleRevision => Volatile.Read(ref _selectionLifecycleRevision);
    internal bool IsViewportMutationInProgress => _viewportMutationDepth > 0;

    internal void SelectSingleLine(int lineNumber)
    {
        _selectedLineNumbers.Clear();
        if (lineNumber > 0)
            _selectedLineNumbers.Add(lineNumber);
        SelectionAnchor = SelectionCaret = lineNumber > 0 ? lineNumber : null;
        EndSelectionExtension();
        PublishSelectionChanged();
    }

    internal void ToggleSelectedLine(int lineNumber)
    {
        if (!_selectedLineNumbers.Remove(lineNumber))
            _selectedLineNumbers.Add(lineNumber);
        SelectionAnchor = SelectionCaret = lineNumber;
        EndSelectionExtension();
        PublishSelectionChanged();
    }

    internal void SelectRangeTo(int lineNumber, bool additive, bool continueExtension = false)
    {
        SelectionAnchor ??= SelectionCaret ?? VisibleLines.FirstOrDefault()?.LineNumber ?? GetDisplayLineNumberAt(0);
        if (SelectionAnchor == null)
            return;

        var anchorIndex = GetDisplayIndexForLineNumber(SelectionAnchor.Value);
        var caretIndex = GetDisplayIndexForLineNumber(lineNumber);
        if (anchorIndex == null || caretIndex == null)
            return;

        if (additive && (!continueExtension || _extensionBase == null))
            _extensionBase = new SortedSet<int>(_selectedLineNumbers);
        else if (!additive)
            EndSelectionExtension();

        _selectedLineNumbers.Clear();
        if (additive && _extensionBase != null)
            _selectedLineNumbers.UnionWith(_extensionBase);
        for (var i = Math.Min(anchorIndex.Value, caretIndex.Value); i <= Math.Max(anchorIndex.Value, caretIndex.Value); i++)
        {
            if (GetDisplayLineNumberAt(i) is { } physicalLine)
                _selectedLineNumbers.Add(physicalLine);
        }

        SelectionCaret = lineNumber;
        PublishSelectionChanged();
    }

    internal void ApplyUserSelectionChanges(IEnumerable<int> added, IEnumerable<int> removed)
    {
        var addedLines = added.ToArray();
        _selectedLineNumbers.ExceptWith(removed);
        _selectedLineNumbers.UnionWith(addedLines);
        if (addedLines.Length > 0)
        {
            SelectionCaret = addedLines[^1];
            SelectionAnchor ??= SelectionCaret;
        }
        EndSelectionExtension();
        PublishSelectionChanged();
    }

    internal void EndSelectionExtension() => _extensionBase = null;

    internal Task RequestSelectionViewportAsync(int lineNumber)
    {
        var displayIndex = GetDisplayIndexForLineNumber(lineNumber);
        if (displayIndex == null)
            return Task.CompletedTask;

        var start = ScrollPosition;
        if (displayIndex.Value < start)
            start = displayIndex.Value;
        else if (displayIndex.Value >= start + ViewportLineCount)
            start = displayIndex.Value - ViewportLineCount + 1;
        return RequestScrollTo(start);
    }

    private void ResetSelection()
    {
        Interlocked.Increment(ref _selectionLifecycleRevision);
        SelectSingleLine(-1);
    }

    private void HandleSelectionGenerationChanged()
    {
        var current = CurrentGenerationToken;
        if (_selectionGenerationToken.IsKnown && current != _selectionGenerationToken)
            ResetSelection();
        _selectionGenerationToken = current;
    }

    private void PublishSelectionChanged()
    {
        _selectionRevision++;
        OnPropertyChanged(nameof(SelectionRevision));
    }

    internal void MutateVisibleLines(Action mutation)
    {
        _viewportMutationDepth++;
        try { mutation(); }
        finally
        {
            _viewportMutationDepth--;
            PublishSelectionChanged();
        }
    }

    // A lease protects index ownership. The existing reader also checks file identity
    // and truncation when opening each batch; physical contents are not a snapshot.
    internal async Task CopySelectedLinesAsync(Action<string> publishText, CancellationToken ct = default)
    {
        var snapshot = await InvokeOnUiAsync(() => (Lines: _selectedLineNumbers.ToArray(),
            Lifecycle: SelectionLifecycleRevision, Session: _session, Generation: CurrentGenerationToken,
            ContentVersion: SearchContentVersion, Encoding: Encoding)).ConfigureAwait(false);
        if (snapshot.Lines.Length == 0 || IsShutdownOrDisposed)
            return;

        bool IsCompatible() => !IsShutdownOrDisposed && ReferenceEquals(snapshot.Session, _session) &&
            SelectionLifecycleRevision == snapshot.Lifecycle && SearchContentVersion == snapshot.ContentVersion &&
            Encoding == snapshot.Encoding && GenerationsCompatible(snapshot.Generation, CurrentGenerationToken);

        try
        {
            var text = await snapshot.Session.WithLineIndexLeaseAsync(async (index, encoding, innerCt) =>
            {
                if (!IsCompatible() || !GenerationsCompatible(snapshot.Generation, index.GenerationToken))
                    throw new IOException("The selected file contents changed before Copy completed.");

                var builder = new StringBuilder();
                for (var offset = 0; offset < snapshot.Lines.Length;)
                {
                    var count = 1;
                    while (offset + count < snapshot.Lines.Length && count < 1024 &&
                        snapshot.Lines[offset + count] == snapshot.Lines[offset] + count)
                        count++;

                    var lines = await snapshot.Session.ReadLinesOffUiAsync(index, snapshot.Lines[offset] - 1,
                        count, encoding, innerCt).ConfigureAwait(false);
                    if (lines.Count != count)
                        throw new IOException("Not all selected lines could be read. The clipboard was left unchanged.");
                    if (!IsCompatible())
                        throw new IOException("The selected file contents changed before Copy completed.");
                    foreach (var line in lines)
                    {
                        if (builder.Length > 0 || offset > 0)
                            builder.Append(Environment.NewLine);
                        builder.Append(line);
                        offset++;
                    }
                }
                return builder.ToString();
            }, ct).ConfigureAwait(false);

            if (text == null)
                throw new IOException("The selected file contents are no longer available.");
            await InvokeOnUiAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (!IsCompatible())
                    throw new IOException("The selected file contents changed before Copy completed.");
                publishText(text);
            }).ConfigureAwait(false);
        }
        catch (Exception) when (IsShutdownOrDisposed)
        {
            // Closing a tab during a read is expected and must leave the clipboard alone.
        }
    }

    private static bool GenerationsCompatible(FileGenerationToken first, FileGenerationToken second)
        => !first.IsKnown || !second.IsKnown || first == second;
}
