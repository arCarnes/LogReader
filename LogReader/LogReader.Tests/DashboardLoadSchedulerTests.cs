namespace LogReader.Tests;

using LogReader.App.Services;

public sealed class DashboardLoadSchedulerTests
{
    [Fact]
    public async Task AllScopes_ShareTheSameRootCapacity()
    {
        using var scheduler = new DashboardLoadScheduler();
        scheduler.SetForeground("A");
        using var first = await scheduler.AcquireAsync(@"C:\fixture\a", () => new[] { "A" }, false, default);
        using var second = await scheduler.AcquireAsync(@"C:\fixture\b", () => new[] { "A" }, false, default);
        var third = scheduler.AcquireAsync(@"C:\fixture\c", () => new[] { "B" }, false, default);
        Assert.False(third.IsCompleted);
        first.Dispose();
        using var admitted = await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SelectedPreview_PrecedesPendingIndexAndRequestsCooperativeYield()
    {
        using var scheduler = new DashboardLoadScheduler();
        scheduler.SetForeground("A");
        using var first = await scheduler.AcquireAsync(@"C:\fixture\a", () => new[] { "A" }, false, default);
        using var second = await scheduler.AcquireAsync(@"C:\fixture\b", () => new[] { "A" }, false, default);
        var index = scheduler.AcquireAsync(@"C:\fixture\c", () => new[] { "A" }, false, default);
        var preview = scheduler.AcquireAsync(@"C:\fixture\a", () => new[] { "A" }, true, default);
        Assert.True(first.ShouldYield());
        second.Dispose();
        using var previewLease = await preview.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(index.IsCompleted);
        previewLease.Dispose();
        using var indexLease = await index.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SwitchingScopes_DemotesRunningWorkAndPromotesWaitingForeground()
    {
        using var scheduler = new DashboardLoadScheduler();
        scheduler.SetForeground("A");
        using var first = await scheduler.AcquireAsync(@"C:\fixture\a", () => new[] { "A" }, false, default);
        using var second = await scheduler.AcquireAsync(@"C:\fixture\a2", () => new[] { "A" }, false, default);
        var background = scheduler.AcquireAsync(@"C:\fixture\c", () => new[] { "C" }, false, default);
        var next = scheduler.AcquireAsync(@"C:\fixture\b", () => new[] { "B" }, false, default);
        scheduler.SetForeground("B");
        Assert.True(first.ShouldYield());
        first.Dispose();
        using var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(background.IsCompleted);
        second.Dispose();
        using var backgroundLease = await background.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BackgroundLane_IsSingleAndRetainsArrivalOrder()
    {
        using var scheduler = new DashboardLoadScheduler();
        using var first = await scheduler.AcquireAsync(@"\\host-a\share\a", () => new[] { "A" }, false, default);
        var second = scheduler.AcquireAsync(@"\\host-b\share\b", () => new[] { "B" }, false, default);
        var third = scheduler.AcquireAsync(@"\\host-c\share\c", () => new[] { "C" }, false, default);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        first.Dispose();
        using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(third.IsCompleted);
        secondLease.Dispose();
        using var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CanceledWaiter_DoesNotBlockTheNextPreview()
    {
        using var scheduler = new DashboardLoadScheduler();
        scheduler.SetForeground("A");
        using var first = await scheduler.AcquireAsync(@"C:\fixture\a", () => new[] { "A" }, false, default);
        using var second = await scheduler.AcquireAsync(@"C:\fixture\b", () => new[] { "A" }, false, default);
        using var cancellation = new CancellationTokenSource();
        var canceled = scheduler.AcquireAsync(@"C:\fixture\a", () => new[] { "A" }, true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        var next = scheduler.AcquireAsync(@"C:\fixture\a", () => new[] { "A" }, true, default);
        first.Dispose();
        using var admitted = await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundOrder_UsesCycleArrivalRatherThanIoArrival_AndReloadGetsANewPlace(bool reload)
    {
        using var scheduler = new DashboardLoadScheduler();
        using var blocker = await scheduler.AcquireAsync(@"C:\fixture\blocker", () => new[] { "blocker" }, false, default);
        scheduler.BeginCycle("A");
        scheduler.BeginCycle("B");
        if (reload) scheduler.BeginCycle("A");
        var secondRequested = scheduler.AcquireAsync(@"C:\fixture\b", () => new[] { "B" }, false, default);
        var firstRequested = scheduler.AcquireAsync(@"C:\fixture\a", () => new[] { "A" }, false, default);
        blocker.Dispose();
        var first = reload ? secondRequested : firstRequested;
        var second = reload ? firstRequested : secondRequested;
        using var firstLease = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.IsCompleted);
        firstLease.Dispose();
        using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MultipleHosts_StillRespectTheGlobalCapacity()
    {
        using var scheduler = new DashboardLoadScheduler();
        scheduler.SetForeground("A");
        var tasks = Enumerable.Range(0, 30).Select(i => scheduler.AcquireAsync($@"\\host-{i}\share\file",
            () => new[] { "A" }, false, default)).ToArray();
        Assert.Equal(24, tasks.Count(task => task.IsCompletedSuccessfully));
        var active = await Task.WhenAll(tasks.Where(task => task.IsCompletedSuccessfully));
        scheduler.Dispose();
        foreach (var lease in active) lease.Dispose();
        foreach (var task in tasks.Where(task => task.IsCanceled))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
