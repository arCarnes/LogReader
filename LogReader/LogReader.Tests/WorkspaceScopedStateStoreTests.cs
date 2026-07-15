namespace LogReader.Tests;

using LogReader.App.Services;

public class WorkspaceScopedStateStoreTests
{
    [Fact]
    public void ActivateScope_ReturnsDefensiveCloneAndConsumesStoredState()
    {
        var scopeA = WorkspaceScopeKey.FromDashboardId("a");
        var scopeB = WorkspaceScopeKey.FromDashboardId("b");
        var store = CreateStore(scopeA);
        var activeState = new TestState("scope-a", [1, 2, 3]);

        Assert.True(store.BeginScopeChange(scopeB, activeState));
        activeState.Values.Add(4);
        store.ActivateScope(scopeB);

        var restored = store.ActivateScope(scopeA);

        Assert.Equal("scope-a", restored.Metadata);
        Assert.Equal([1, 2, 3], restored.Values);
        Assert.NotSame(activeState.Values, restored.Values);

        store.ActivateScope(scopeB);
        Assert.Null(store.TryGetScopeState(scopeA, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void ActivateScope_WhenCloneFails_RetainsStoredState()
    {
        var scopeA = WorkspaceScopeKey.FromDashboardId("a");
        var scopeB = WorkspaceScopeKey.FromDashboardId("b");
        var failClone = false;
        var store = new WorkspaceScopedStateStore<TestState>(
            scopeA,
            static () => new TestState("default", []),
            state => failClone
                ? throw new InvalidOperationException("clone failed")
                : CloneState(state));

        Assert.True(store.BeginScopeChange(scopeB, new TestState("scope-a", [7])));
        store.ActivateScope(scopeB);
        failClone = true;

        Assert.Throws<InvalidOperationException>(() => store.ActivateScope(scopeA));
        Assert.Equal(scopeB, store.ActiveScopeKey);

        failClone = false;
        var restored = store.ActivateScope(scopeA);
        Assert.Equal("scope-a", restored.Metadata);
        Assert.Equal([7], restored.Values);
    }

    [Fact]
    public void BeginScopeChange_AfterActivation_RecapturesLatestActiveState()
    {
        var scopeA = WorkspaceScopeKey.FromDashboardId("a");
        var scopeB = WorkspaceScopeKey.FromDashboardId("b");
        var store = CreateStore(scopeA);

        Assert.True(store.BeginScopeChange(scopeB, new TestState("initial", [1])));
        store.ActivateScope(scopeB);
        store.ActivateScope(scopeA);

        Assert.True(store.BeginScopeChange(scopeB, new TestState("latest", [2, 3])));
        store.ActivateScope(scopeB);
        var restored = store.ActivateScope(scopeA);

        Assert.Equal("latest", restored.Metadata);
        Assert.Equal([2, 3], restored.Values);
    }

    [Fact]
    public void Persist_StoresDefensiveCloneUntilActivation()
    {
        var scope = WorkspaceScopeKey.FromDashboardId("active");
        var otherScope = WorkspaceScopeKey.FromDashboardId("other");
        var store = CreateStore(scope);
        var state = new TestState("persisted", [10]);

        store.Persist(state);
        state.Values.Add(11);
        store.ActivateScope(otherScope);
        var restored = store.ActivateScope(scope);

        Assert.Equal("persisted", restored.Metadata);
        Assert.Equal([10], restored.Values);
    }

    private static WorkspaceScopedStateStore<TestState> CreateStore(WorkspaceScopeKey activeScopeKey)
        => new(
            activeScopeKey,
            static () => new TestState("default", []),
            CloneState);

    private static TestState CloneState(TestState state)
        => new(state.Metadata, state.Values.ToList());

    private sealed record TestState(string Metadata, List<int> Values);
}
