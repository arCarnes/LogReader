namespace LogReader.App.Services;

internal sealed class WorkspaceScopedStateStore<TState> where TState : class
{
    private readonly Dictionary<WorkspaceScopeKey, TState> _states = new();
    private readonly Func<TState> _createDefaultState;
    private readonly Func<TState, TState> _cloneState;

    public WorkspaceScopedStateStore(
        WorkspaceScopeKey activeScopeKey,
        Func<TState> createDefaultState,
        Func<TState, TState> cloneState)
    {
        _createDefaultState = createDefaultState;
        _cloneState = cloneState;
        ActiveScopeKey = activeScopeKey;
    }

    public WorkspaceScopeKey ActiveScopeKey { get; private set; }

    public WorkspaceScopeKey? PendingScopeKey { get; private set; }

    public bool BeginScopeChange(WorkspaceScopeKey nextScopeKey, TState activeState)
    {
        if (nextScopeKey.Equals(ActiveScopeKey))
            return false;

        _states[ActiveScopeKey] = _cloneState(activeState);
        PendingScopeKey = nextScopeKey;
        return true;
    }

    public TState ActivateScope(WorkspaceScopeKey scopeKey)
    {
        ActiveScopeKey = scopeKey;
        PendingScopeKey = null;
        return _states.TryGetValue(scopeKey, out var existingState)
            ? _cloneState(existingState)
            : _createDefaultState();
    }

    public void Persist(TState activeState)
    {
        _states[ActiveScopeKey] = _cloneState(activeState);
    }

    public void ResetScope(WorkspaceScopeKey scopeKey)
    {
        _states.Remove(scopeKey);
        if (PendingScopeKey != null && PendingScopeKey.Value.Equals(scopeKey))
            PendingScopeKey = null;
    }

    public TState? TryGetScopeState(WorkspaceScopeKey scopeKey, Func<TState> captureActiveState)
    {
        if (PendingScopeKey != null && scopeKey.Equals(ActiveScopeKey))
            return null;

        if (scopeKey.Equals(ActiveScopeKey))
            return _cloneState(captureActiveState());

        return _states.TryGetValue(scopeKey, out var state)
            ? _cloneState(state)
            : null;
    }
}
