namespace LogReader.App.ViewModels;

internal sealed class SearchResultHitRowCache
{
    internal const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly Dictionary<CacheKey, CacheEntry> _entries = new();
    private readonly LinkedList<CacheKey> _lru = new();

    public SearchResultHitRowCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    internal int Count => _entries.Count;

    public SearchResultHitRowViewModel GetOrCreate(
        FileSearchResultViewModel fileResult,
        long lineNumber,
        string lineText,
        Func<SearchResultHitRowViewModel> createRow)
    {
        var key = new CacheKey(fileResult, lineNumber, lineText);
        if (_entries.TryGetValue(key, out var existingEntry))
        {
            _lru.Remove(existingEntry.Node);
            _lru.AddLast(existingEntry.Node);
            return existingEntry.Row;
        }

        var row = createRow();
        var node = _lru.AddLast(key);
        _entries[key] = new CacheEntry(row, node);
        Trim();
        return row;
    }

    public void Remove(FileSearchResultViewModel fileResult, long lineNumber, string lineText)
    {
        var key = new CacheKey(fileResult, lineNumber, lineText);
        if (!_entries.Remove(key, out var entry))
            return;

        _lru.Remove(entry.Node);
    }

    public void Clear()
    {
        _entries.Clear();
        _lru.Clear();
    }

    private void Trim()
    {
        while (_entries.Count > _capacity && _lru.First != null)
        {
            var key = _lru.First.Value;
            _lru.RemoveFirst();
            _entries.Remove(key);
        }
    }

    private readonly record struct CacheKey(
        FileSearchResultViewModel FileResult,
        long LineNumber,
        string LineText);

    private sealed record CacheEntry(
        SearchResultHitRowViewModel Row,
        LinkedListNode<CacheKey> Node);
}
