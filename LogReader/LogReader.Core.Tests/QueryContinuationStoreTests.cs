namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed class QueryContinuationStoreTests
{

    [Fact]
    public async Task RevisedDefaultResponseBudgetRetainsBoundedWorkingAdmission()
    {
        var limits = LogQueryEffectiveLimits.Default;
        Assert.Equal(800_000, limits.MaximumResponseCharacters);
        Assert.Equal(256L * 1024 * 1024, limits.MaximumContinuationBytes);
        using var store = new QueryContinuationStore(limits, () => DateTimeOffset.UtcNow);
        using var first = await store.AcquireAsync(null, "search", "first", "catalog", default);
        using var second = await store.AcquireAsync(null, "count", "second", "catalog", default);
        var overflow = await Assert.ThrowsAsync<ContinuationException>(() =>
            store.AcquireAsync(null, "search", "third", "catalog", default));
        Assert.Equal("continuation_capacity_exceeded", overflow.Code);
    }
    [Fact]
    public async Task WorkingAdmissionAndCommitTransferRemainWithinGlobalCapacity()
    {
        var limits = LogQueryEffectiveLimits.Default with
        {
            MaximumContinuationSessionBytes = 1024,
            MaximumSearchLineBytes = 2,
            MaximumResponseCharacters = 10,
            MaximumContinuationBytes = 526_000
        };
        using var store = new QueryContinuationStore(limits, () => DateTimeOffset.UtcNow);
        using (var active = await store.AcquireAsync(null, "search", "first", "catalog", default))
        {
            Assert.Equal("continuation_capacity_exceeded", (await Assert.ThrowsAsync<ContinuationException>(() =>
                store.AcquireAsync(null, "count", "second", "catalog", default))).Code);
            active.Commit("state", "reply", 500, false);
        }
        using (var next = await store.AcquireAsync(null, "count", "second", "catalog", default))
            next.Commit("terminal", "reply", 500, true);
        // Initial terminal calls have no input cursor to replay and must release their slots.
        using var available = await store.AcquireAsync(null, "count", "third", "catalog", default);
    }

    [Fact]
    public async Task RetryReplaysOnlyImmediatelyPreviousRevision()
    {
        using var store = new QueryContinuationStore(LogQueryEffectiveLimits.Default, () => DateTimeOffset.UtcNow);
        string cursor;
        using (var initial = await store.AcquireAsync(null, "search", "query", "catalog", default))
        {
            cursor = initial.NextCursor;
            initial.Commit("first", "first response", 100, false);
        }
        string next;
        using (var advance = await store.AcquireAsync(cursor, "search", "query", "catalog", default))
        {
            Assert.Equal("first", advance.State);
            next = advance.NextCursor;
            advance.Commit("second", "second response", 100, false);
        }
        using (var replay = await store.AcquireAsync(cursor, "search", "query", "catalog", default))
        {
            Assert.True(replay.IsReplay);
            Assert.Equal("second response", replay.ReplayResult);
        }
        using (var terminal = await store.AcquireAsync(next, "search", "query", "catalog", default))
            terminal.Commit("done", "terminal response", 100, true);
        var stale = await Assert.ThrowsAsync<ContinuationException>(() =>
            store.AcquireAsync(cursor, "search", "query", "catalog", default));
        Assert.Equal("stale_query_cursor", stale.Code);
        using var terminalReplay = await store.AcquireAsync(next, "search", "query", "catalog", default);
        Assert.Equal("terminal response", terminalReplay.ReplayResult);
    }

    [Fact]
    public async Task ExpiryCapacityAndBindingAreExplicit()
    {
        var now = DateTimeOffset.UtcNow;
        using var store = new QueryContinuationStore(LogQueryEffectiveLimits.Default with
        {
            MaximumContinuationSessions = 1,
            ContinuationIdleMilliseconds = 10
        }, () => now);
        string cursor;
        using (var initial = await store.AcquireAsync(null, "count", "query", "catalog", default))
        {
            cursor = initial.NextCursor;
            initial.Commit("state", "response", 100, false);
        }
        foreach (var (operation, query, catalog, code) in new[]
        {
            ("search", "query", "catalog", "mismatched_query_cursor"),
            ("count", "different", "catalog", "mismatched_query_cursor"),
            ("count", "query", "changed", "stale_query_cursor")
        })
        {
            var error = await Assert.ThrowsAsync<ContinuationException>(() =>
                store.AcquireAsync(cursor, operation, query, catalog, default));
            Assert.Equal(code, error.Code);
        }
        Assert.Equal("continuation_capacity_exceeded", (await Assert.ThrowsAsync<ContinuationException>(() =>
            store.AcquireAsync(null, "search", "other", "catalog", default))).Code);
        now = now.AddMilliseconds(11);
        Assert.Equal("expired_query_cursor", (await Assert.ThrowsAsync<ContinuationException>(() =>
            store.AcquireAsync(cursor, "count", "query", "catalog", default))).Code);
        using var fresh = await store.AcquireAsync(null, "search", "other", "catalog", default);
    }

    [Fact]
    public async Task UncommittedTransactionLeavesCheckpointAndRestartRejectsToken()
    {
        using var store = new QueryContinuationStore(LogQueryEffectiveLimits.Default, () => DateTimeOffset.UtcNow);
        string cursor;
        using (var initial = await store.AcquireAsync(null, "search", "query", "catalog", default))
        {
            cursor = initial.NextCursor;
            initial.Commit("checkpoint", "response", 100, false);
        }
        using (var abandoned = await store.AcquireAsync(cursor, "search", "query", "catalog", default))
            Assert.Equal("checkpoint", abandoned.State);
        using (var retry = await store.AcquireAsync(cursor, "search", "query", "catalog", default))
            Assert.False(retry.IsReplay);
        using var other = new QueryContinuationStore(LogQueryEffectiveLimits.Default, () => DateTimeOffset.UtcNow);
        Assert.Equal("invalid_query_cursor", (await Assert.ThrowsAsync<ContinuationException>(() =>
            other.AcquireAsync(cursor, "search", "query", "catalog", default))).Code);
        Assert.Equal("invalid_query_cursor", (await Assert.ThrowsAsync<ContinuationException>(() =>
            store.AcquireAsync(cursor + "!", "search", "query", "catalog", default))).Code);
    }
}
