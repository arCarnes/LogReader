namespace LogReader.App.Services;

using System.Collections.Specialized;
using LogReader.App.ViewModels;

internal sealed record TabMemberRefreshRequest(
    bool RequiresFullRefresh,
    IReadOnlyDictionary<string, string> ChangedFilePaths);

internal sealed class TabCollectionRefreshCoordinator
{
    private readonly Dictionary<string, string> _pendingMemberRefreshFilePaths = new(StringComparer.Ordinal);
    private bool _pendingFullMemberRefresh;
    private bool _tabCollectionChangePending;
    private int _suppressionDepth;

    public void Begin()
    {
        _suppressionDepth++;
    }

    public bool TryHandleCollectionChanged(
        NotifyCollectionChangedEventArgs e,
        bool hasActiveModifiers,
        out TabMemberRefreshRequest? request)
    {
        if (_suppressionDepth > 0)
        {
            _tabCollectionChangePending = true;
            QueuePendingMemberRefresh(e, hasActiveModifiers);
            request = null;
            return false;
        }

        request = CreateRefreshRequest(e, hasActiveModifiers);
        return true;
    }

    public TabMemberRefreshRequest? End(bool hasActiveModifiers)
    {
        _suppressionDepth = Math.Max(0, _suppressionDepth - 1);
        if (_suppressionDepth > 0 || !_tabCollectionChangePending)
            return null;

        _tabCollectionChangePending = false;
        return FlushPendingMemberRefresh(hasActiveModifiers);
    }

    private void QueuePendingMemberRefresh(NotifyCollectionChangedEventArgs e, bool hasActiveModifiers)
    {
        if (_pendingFullMemberRefresh || hasActiveModifiers || RequiresFullMemberRefresh(e))
        {
            _pendingFullMemberRefresh = true;
            _pendingMemberRefreshFilePaths.Clear();
            return;
        }

        MergePendingMemberRefreshFilePaths(e.NewItems);
        MergePendingMemberRefreshFilePaths(e.OldItems);
    }

    private TabMemberRefreshRequest FlushPendingMemberRefresh(bool hasActiveModifiers)
    {
        if (_pendingFullMemberRefresh || hasActiveModifiers)
        {
            _pendingFullMemberRefresh = false;
            _pendingMemberRefreshFilePaths.Clear();
            return new TabMemberRefreshRequest(true, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var changedFilePaths = _pendingMemberRefreshFilePaths.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        _pendingMemberRefreshFilePaths.Clear();
        return new TabMemberRefreshRequest(false, changedFilePaths);
    }

    private static TabMemberRefreshRequest CreateRefreshRequest(NotifyCollectionChangedEventArgs e, bool hasActiveModifiers)
    {
        if (hasActiveModifiers || RequiresFullMemberRefresh(e))
            return new TabMemberRefreshRequest(true, new Dictionary<string, string>(StringComparer.Ordinal));

        return new TabMemberRefreshRequest(false, CollectChangedTabFilePaths(e.NewItems, e.OldItems));
    }

    private static bool RequiresFullMemberRefresh(NotifyCollectionChangedEventArgs e)
        => e.Action is NotifyCollectionChangedAction.Reset
            or NotifyCollectionChangedAction.Move;

    private static Dictionary<string, string> CollectChangedTabFilePaths(
        System.Collections.IList? newItems,
        System.Collections.IList? oldItems)
    {
        var changedFilePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        AddChangedTabFilePaths(changedFilePaths, newItems);
        AddChangedTabFilePaths(changedFilePaths, oldItems);
        return changedFilePaths;
    }

    private void MergePendingMemberRefreshFilePaths(System.Collections.IList? items)
        => AddChangedTabFilePaths(_pendingMemberRefreshFilePaths, items);

    private static void AddChangedTabFilePaths(
        IDictionary<string, string> destination,
        System.Collections.IList? items)
    {
        if (items == null)
            return;

        foreach (var item in items.OfType<LogTabViewModel>())
            destination[item.FileId] = item.FilePath;
    }
}
