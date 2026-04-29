namespace LogReader.App.ViewModels;

using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

public sealed class SearchResultsFlatCollection : IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private readonly List<GroupSegment> _segments = new();
    private readonly Dictionary<SearchResultFileHeaderRowViewModel, int> _headerIndexes = new();
    private readonly Dictionary<FileSearchResultViewModel, GroupSegment> _segmentsByFileResult = new();
    private int _count;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count => _count;

    public bool IsSynchronized => false;

    public object SyncRoot { get; } = new();

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public object? this[int index]
    {
        get
        {
            var segment = GetSegment(index, out var offset);
            return offset == 0
                ? segment.HeaderRow
                : segment.FileResult.GetHitRow(offset - 1);
        }
        set => throw new NotSupportedException();
    }

    public void Refresh(IReadOnlyList<FileSearchResultViewModel> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        _segments.Clear();
        _headerIndexes.Clear();
        _segmentsByFileResult.Clear();
        _count = 0;

        foreach (var result in results)
        {
            var visibleRowCount = 1 + (result.IsExpanded ? result.HitCount : 0);
            var segment = new GroupSegment(result, result.HeaderRow, _count, visibleRowCount);
            _segments.Add(segment);
            _headerIndexes[result.HeaderRow] = _count;
            _segmentsByFileResult[result] = segment;
            _count += visibleRowCount;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value)
    {
        return value switch
        {
            SearchResultFileHeaderRowViewModel headerRow => IndexOfHeader(headerRow),
            SearchResultHitRowViewModel hitRow => IndexOfHit(hitRow),
            _ => -1
        };
    }

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var sourceIndex = 0; sourceIndex < _count; sourceIndex++)
            array.SetValue(this[sourceIndex], index + sourceIndex);
    }

    public IEnumerator GetEnumerator()
    {
        for (var index = 0; index < _count; index++)
            yield return this[index]!;
    }

    private GroupSegment GetSegment(int index, out int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);

        var low = 0;
        var high = _segments.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var segment = _segments[mid];
            if (index < segment.StartIndex)
            {
                high = mid - 1;
                continue;
            }

            if (index >= segment.StartIndex + segment.RowCount)
            {
                low = mid + 1;
                continue;
            }

            offset = index - segment.StartIndex;
            return segment;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    private int IndexOfHeader(SearchResultFileHeaderRowViewModel headerRow)
        => _headerIndexes.TryGetValue(headerRow, out var index) ? index : -1;

    private int IndexOfHit(SearchResultHitRowViewModel hitRow)
    {
        if (!_segmentsByFileResult.TryGetValue(hitRow.FileResult, out var segment) ||
            !segment.FileResult.IsExpanded)
        {
            return -1;
        }

        if (hitRow.HitIndex < 0 || hitRow.HitIndex >= segment.FileResult.HitCount)
            return -1;

        return segment.StartIndex + 1 + hitRow.HitIndex;
    }

    private sealed record GroupSegment(
        FileSearchResultViewModel FileResult,
        SearchResultFileHeaderRowViewModel HeaderRow,
        int StartIndex,
        int RowCount);
}
