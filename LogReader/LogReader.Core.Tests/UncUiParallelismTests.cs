namespace LogReader.Core.Tests;

using LogReader.Core;

public sealed class UncUiParallelismTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Plans_NormalizeAliasesAndBoundOneHostAcrossShares(int operation)
    {
        string[] paths = [@"\\SERVER\share-a\one", @"\\server\SHARE-A\two",
            @"\\?\UNC\Server\share-a\three", @"\\server\share-a\four",
            @"\\server\share-b\one", @"\\SERVER\share-b\two",
            @"\\?\UNC\server\SHARE-B\three", @"\\server\share-b\four"];
        var plan = AdaptiveParallelismPolicy.CreatePlan((AdaptiveParallelismOperation)operation, paths);
        Assert.Equal(4, plan.GlobalLimit);
        var host = Assert.Single(plan.Groups, group => group.Kind == ParallelismGroupKind.UncHost);
        Assert.Equal(@"\\server", host.Key);
        Assert.Equal(8, host.TargetCount);
        Assert.Equal(4, host.Limit);
        Assert.All(plan.Targets, target => Assert.Equal(host.Key, target.TopLevelGroupKey));
        var shares = plan.Groups.Where(group => group.Kind == ParallelismGroupKind.UncShare).ToArray();
        Assert.Equal(2, shares.Length);
        Assert.All(shares, share => { Assert.Equal(4, share.TargetCount); Assert.Equal(4, share.Limit); Assert.Equal(host.Key, share.ParentKey); });
        Assert.Equal([@"\\server\share-a", @"\\server\share-b"], shares.Select(share => share.Key));
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(1, 32)]
    [InlineData(2, 32)]
    public void Plans_PreserveGlobalLocalAndUnknownLimits(int operation, int globalCap)
    {
        var paths = Enumerable.Range(0, 40).SelectMany(host => Enumerable.Range(0, 6)
            .Select(file => $@"\\host-{host}\share\file-{file}"))
            .Concat(Enumerable.Repeat(@"C:\fixture\local", 6)).Concat(["relative-one", "relative-two"]).ToArray();
        var plan = AdaptiveParallelismPolicy.CreatePlan((AdaptiveParallelismOperation)operation, paths);
        Assert.Equal(globalCap, plan.GlobalLimit);
        Assert.All(plan.Groups.Where(group => group.Kind is ParallelismGroupKind.UncHost or ParallelismGroupKind.UncShare),
            group => Assert.Equal(4, group.Limit));
        Assert.Equal(2, Assert.Single(plan.Groups, group => group.Kind == ParallelismGroupKind.LocalRoot).Limit);
        Assert.Equal(1, Assert.Single(plan.Groups, group => group.Kind == ParallelismGroupKind.Unknown).Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Gates_MixedTopologyAndQueuedCancellationRetainEveryBound(int operation)
    {
        var paths = Enumerable.Range(0, 6).Select(i => $@"\\host-a\share\a-{i}")
            .Concat(Enumerable.Range(0, 6).Select(i => $@"\\host-b\share\b-{i}"))
            .Concat(Enumerable.Range(0, 4).Select(i => $@"C:\fixture\local-{i}"))
            .Concat(["relative-one", "relative-two"]).ToArray();
        var plan = AdaptiveParallelismPolicy.CreatePlan((AdaptiveParallelismOperation)operation, paths);
        Assert.Equal(11, plan.GlobalLimit); // 4 + 4 + 2 + 1; groups are bounds, never multipliers.
        using var gates = AdaptiveParallelismGateSet.Create(plan);
        using var cancellation = new CancellationTokenSource();
        var requests = new List<Task<AdaptiveParallelismGateSet.GateLease>>();
        try
        {
            for (var i = 0; i < 4; i++) await RequestAsync(i).WaitAsync(TimeSpan.FromSeconds(5));
            using (var queuedCancellation = new CancellationTokenSource())
            {
                var queued = gates.AcquireAsync(plan.Targets[4], queuedCancellation.Token);
                requests.Add(queued);
                try
                {
                    Assert.False(queued.IsCompleted);
                    queuedCancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
                }
                finally { queuedCancellation.Cancel(); }
            }
            for (var i = 6; i < 10; i++) await RequestAsync(i).WaitAsync(TimeSpan.FromSeconds(5));
            await RequestAsync(12).WaitAsync(TimeSpan.FromSeconds(5));
            await RequestAsync(13).WaitAsync(TimeSpan.FromSeconds(5));
            await RequestAsync(16).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(11, requests.Count(task => task.IsCompletedSuccessfully));
            Assert.False(RequestAsync(4).IsCompleted);
            Assert.False(RequestAsync(10).IsCompleted);
            Assert.False(RequestAsync(14).IsCompleted);
            Assert.False(RequestAsync(17).IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var request in requests)
            {
                try { (await request.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }
        // Cancellation releases any partially acquired global/group capacity; a fresh full
        // batch after draining exercises every bound again with no cancellation token.
        using var freshCancellation = new CancellationTokenSource();
        var fresh = new List<Task<AdaptiveParallelismGateSet.GateLease>>();
        try
        {
            foreach (var i in new[] { 0, 1, 2, 3, 6, 7, 8, 9, 12, 13, 16 })
            {
                var request = gates.AcquireAsync(plan.Targets[i], freshCancellation.Token);
                fresh.Add(request);
                await request.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(11, fresh.Count);
        }
        finally
        {
            freshCancellation.Cancel();
            foreach (var request in fresh)
            {
                try { (await request.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
                catch (OperationCanceledException) when (freshCancellation.IsCancellationRequested) { }
            }
        }

        Task<AdaptiveParallelismGateSet.GateLease> RequestAsync(int index)
        {
            var request = gates.AcquireAsync(plan.Targets[index], cancellation.Token);
            requests.Add(request);
            return request;
        }
    }
}
