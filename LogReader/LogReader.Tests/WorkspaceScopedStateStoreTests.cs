namespace LogReader.Tests;

using LogReader.App.Services;

public class WorkspaceScopedStateStoreTests
{
    [Fact]
    public void ActivateScope_EvictsLeastRecentlyUsedInactivePayload_AndPreservesMetadata()
    {
        var scopeA = WorkspaceScopeKey.FromDashboardId("a");
        var scopeB = WorkspaceScopeKey.FromDashboardId("b");
        var scopeC = WorkspaceScopeKey.FromDashboardId("c");
        var store = CreateStore(scopeA, maxItems: 6, maxBytes: 600);

        Assert.True(store.BeginScopeChange(scopeB, new TestState("scope-a", 6, 600)));
        store.ActivateScope(scopeB);
        Assert.True(store.BeginScopeChange(scopeC, new TestState("scope-b", 6, 600)));
        store.ActivateScope(scopeC);

        var evictedA = store.TryGetScopeState(scopeA, () => throw new InvalidOperationException());
        var retainedB = store.TryGetScopeState(scopeB, () => throw new InvalidOperationException());

        Assert.NotNull(evictedA);
        Assert.Equal("scope-a", evictedA.Metadata);
        Assert.True(evictedA.WasEvicted);
        Assert.Equal(0, evictedA.ItemCount);
        Assert.NotNull(retainedB);
        Assert.Equal("scope-b", retainedB.Metadata);
        Assert.False(retainedB.WasEvicted);
        Assert.Equal(6, retainedB.ItemCount);
        Assert.Equal(new WorkspaceStateRetentionSize(6, 600), store.GetInactiveRetentionSize());
    }

    [Fact]
    public void Persist_DoesNotChargeActiveScopeAgainstInactiveBudget()
    {
        var activeScope = WorkspaceScopeKey.FromDashboardId("active");
        var store = CreateStore(activeScope, maxItems: 1, maxBytes: 1);
        var activeState = new TestState("selected", 100, 10_000);

        store.Persist(activeState);

        var restored = store.TryGetScopeState(activeScope, () => activeState);
        Assert.NotNull(restored);
        Assert.Equal(100, restored.ItemCount);
        Assert.False(restored.WasEvicted);
        Assert.Equal(default, store.GetInactiveRetentionSize());
    }

    private static WorkspaceScopedStateStore<TestState> CreateStore(
        WorkspaceScopeKey activeScopeKey,
        long maxItems,
        long maxBytes)
        => new(
            activeScopeKey,
            static () => new TestState("default", 0, 0),
            static state => state with { },
            new WorkspaceStateRetentionPolicy<TestState>(
                maxItems,
                maxBytes,
                static state => new WorkspaceStateRetentionSize(state.ItemCount, state.EstimatedBytes),
                static state => state with
                {
                    ItemCount = 0,
                    EstimatedBytes = 0,
                    WasEvicted = true
                }));

    private sealed record TestState(
        string Metadata,
        long ItemCount,
        long EstimatedBytes,
        bool WasEvicted = false);
}
