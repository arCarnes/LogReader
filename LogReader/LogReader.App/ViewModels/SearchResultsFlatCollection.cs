namespace LogReader.App.ViewModels;

using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

public sealed class SearchResultsFlatCollection : IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private readonly List<GroupSegment> _segments = new();
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
        _count = 0;

        foreach (var result in results)
        {
            var visibleRowCount = 1 + (result.IsExpanded ? result.HitCount : 0);
            _segments.Add(new GroupSegment(result, result.HeaderRow, _count, visibleRowCount));
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

        foreach (var segment in _segments)
        {
            if (index < segment.StartIndex || index >= segment.StartIndex + segment.RowCount)
                continue;

            offset = index - segment.StartIndex;
            return segment;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    private int IndexOfHeader(SearchResultFileHeaderRowViewModel headerRow)
    {
        foreach (var segment in _segments)
        {
            if (ReferenceEquals(segment.HeaderRow, headerRow))
                return segment.StartIndex;
        }

        return -1;
    }

    private int IndexOfHit(SearchResultHitRowViewModel hitRow)
    {
        foreach (var segment in _segments)
        {
            if (!ReferenceEquals(segment.FileResult, hitRow.FileResult) || !segment.FileResult.IsExpanded)
                continue;

            if (hitRow.HitIndex < 0 || hitRow.HitIndex >= segment.FileResult.HitCount)
                return -1;

            return segment.StartIndex + 1 + hitRow.HitIndex;
        }

        return -1;
    }

    private sealed record GroupSegment(
        FileSearchResultViewModel FileResult,
        SearchResultFileHeaderRowViewModel HeaderRow,
        int StartIndex,
        int RowCount);
}
