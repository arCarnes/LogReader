namespace LogReader.App.Services;

internal readonly record struct WorkspaceStateRetentionSize(long ItemCount, long EstimatedBytes)
{
    public static WorkspaceStateRetentionSize operator +(
        WorkspaceStateRetentionSize left,
        WorkspaceStateRetentionSize right)
        => new(left.ItemCount + right.ItemCount, left.EstimatedBytes + right.EstimatedBytes);

    public static WorkspaceStateRetentionSize operator -(
        WorkspaceStateRetentionSize left,
        WorkspaceStateRetentionSize right)
        => new(left.ItemCount - right.ItemCount, left.EstimatedBytes - right.EstimatedBytes);
}

internal sealed record WorkspaceStateRetentionPolicy<TState>(
    long MaxInactiveItemCount,
    long MaxInactiveEstimatedBytes,
    Func<TState, WorkspaceStateRetentionSize> Measure,
    Func<TState, TState> EvictRetainedPayload)
    where TState : class;

internal sealed class WorkspaceScopedStateStore<TState> where TState : class
{
    private readonly Dictionary<WorkspaceScopeKey, TState> _states = new();
    private readonly Dictionary<WorkspaceScopeKey, LinkedListNode<WorkspaceScopeKey>> _retentionNodes = new();
    private readonly LinkedList<WorkspaceScopeKey> _retentionLru = new();
    private readonly Func<TState> _createDefaultState;
    private readonly Func<TState, TState> _cloneState;
    private readonly WorkspaceStateRetentionPolicy<TState>? _retentionPolicy;

    public WorkspaceScopedStateStore(
        WorkspaceScopeKey activeScopeKey,
        Func<TState> createDefaultState,
        Func<TState, TState> cloneState,
        WorkspaceStateRetentionPolicy<TState>? retentionPolicy = null)
    {
        if (retentionPolicy is { MaxInactiveItemCount: < 0 })
            throw new ArgumentOutOfRangeException(nameof(retentionPolicy), "The inactive item budget cannot be negative.");
        if (retentionPolicy is { MaxInactiveEstimatedBytes: < 0 })
            throw new ArgumentOutOfRangeException(nameof(retentionPolicy), "The inactive byte budget cannot be negative.");

        _createDefaultState = createDefaultState;
        _cloneState = cloneState;
        _retentionPolicy = retentionPolicy;
        ActiveScopeKey = activeScopeKey;
    }

    public WorkspaceScopeKey ActiveScopeKey { get; private set; }

    public WorkspaceScopeKey? PendingScopeKey { get; private set; }

    public bool BeginScopeChange(WorkspaceScopeKey nextScopeKey, TState activeState)
    {
        if (nextScopeKey.Equals(ActiveScopeKey))
            return false;

        StoreState(ActiveScopeKey, activeState, isActive: false);
        PendingScopeKey = nextScopeKey;
        return true;
    }

    public TState ActivateScope(WorkspaceScopeKey scopeKey)
    {
        ActiveScopeKey = scopeKey;
        PendingScopeKey = null;
        RemoveFromRetentionLru(scopeKey);
        var activatedState = _states.TryGetValue(scopeKey, out var existingState)
            ? _cloneState(existingState)
            : _createDefaultState();
        TrimInactiveRetainedPayloads();
        return activatedState;
    }

    public void Persist(TState activeState)
    {
        StoreState(ActiveScopeKey, activeState, isActive: true);
    }

    public void ResetScope(WorkspaceScopeKey scopeKey)
    {
        _states.Remove(scopeKey);
        RemoveFromRetentionLru(scopeKey);
        if (PendingScopeKey != null && PendingScopeKey.Value.Equals(scopeKey))
            PendingScopeKey = null;
    }

    public TState? TryGetScopeState(WorkspaceScopeKey scopeKey, Func<TState> captureActiveState)
    {
        if (PendingScopeKey != null && scopeKey.Equals(ActiveScopeKey))
            return null;

        if (scopeKey.Equals(ActiveScopeKey))
            return _cloneState(captureActiveState());

        if (!_states.TryGetValue(scopeKey, out var state))
            return null;

        TouchRetainedState(scopeKey, state);
        return _cloneState(state);
    }

    internal WorkspaceStateRetentionSize GetInactiveRetentionSize()
    {
        if (_retentionPolicy == null)
            return default;

        var total = default(WorkspaceStateRetentionSize);
        foreach (var (scopeKey, state) in _states)
        {
            if (!scopeKey.Equals(ActiveScopeKey))
                total += _retentionPolicy.Measure(state);
        }

        return total;
    }

    private void StoreState(WorkspaceScopeKey scopeKey, TState state, bool isActive)
    {
        var storedState = _cloneState(state);
        _states[scopeKey] = storedState;
        if (isActive)
            RemoveFromRetentionLru(scopeKey);
        else
            TouchRetainedState(scopeKey, storedState);
    }

    private void TouchRetainedState(WorkspaceScopeKey scopeKey, TState state)
    {
        if (_retentionPolicy == null)
            return;

        RemoveFromRetentionLru(scopeKey);
        var size = _retentionPolicy.Measure(state);
        if (size.ItemCount <= 0 && size.EstimatedBytes <= 0)
            return;

        _retentionNodes[scopeKey] = _retentionLru.AddLast(scopeKey);
    }

    private void RemoveFromRetentionLru(WorkspaceScopeKey scopeKey)
    {
        if (!_retentionNodes.Remove(scopeKey, out var node))
            return;

        _retentionLru.Remove(node);
    }

    private void TrimInactiveRetainedPayloads()
    {
        if (_retentionPolicy == null)
            return;

        var total = GetInactiveRetentionSize();
        while ((total.ItemCount > _retentionPolicy.MaxInactiveItemCount ||
                total.EstimatedBytes > _retentionPolicy.MaxInactiveEstimatedBytes) &&
               _retentionLru.First != null)
        {
            var scopeKey = _retentionLru.First.Value;
            RemoveFromRetentionLru(scopeKey);
            if (scopeKey.Equals(ActiveScopeKey) || !_states.TryGetValue(scopeKey, out var state))
                continue;

            var previousSize = _retentionPolicy.Measure(state);
            var evictedState = _retentionPolicy.EvictRetainedPayload(state);
            _states[scopeKey] = evictedState;
            var evictedSize = _retentionPolicy.Measure(evictedState);
            total = total - previousSize + evictedSize;
            TouchRetainedState(scopeKey, evictedState);

            if (evictedSize.Equals(previousSize))
                break;
        }
    }
}
