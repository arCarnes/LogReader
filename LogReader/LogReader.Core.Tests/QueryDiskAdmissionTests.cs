namespace LogReader.Core.Tests;

using LogReader.Infrastructure.Services;

public sealed class QueryDiskAdmissionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public async Task GlobalLimit_BoundsIndependentHostsAndPreservesQueueOrder(int limit)
    {
        await using var fixture = new Fixture(limit);
        var active = new List<IDisposable>();
        for (var i = 0; i < limit; i++) active.Add(await Ready(fixture.Request($@"\\host-{i}\share\file")));
        var older = fixture.Request(@"\\older\share\file");
        var later = fixture.Request(@"\\later\share\file");
        Assert.False(older.IsCompleted);
        Assert.False(later.IsCompleted);
        Assert.Equal(limit, fixture.Admission.GetSnapshot().ActiveOperations);
        active[0].Dispose();
        var admitted = await Ready(older);
        Assert.False(later.IsCompleted);
        admitted.Dispose();
        await Ready(later);
        Assert.Equal(limit, fixture.Admission.GetSnapshot().ActiveOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncHost_FourAcrossAliasesAndShares_DoesNotBlockOtherStorage(bool multipleShares)
    {
        await using var fixture = new Fixture(8);
        var first = await Ready(fixture.Request(@"\\SERVER\share-a\one"));
        await Ready(fixture.Request(@"\\server\SHARE-A\two"));
        await Ready(fixture.Request(@"\\?\UNC\Server\share-a\three"));
        await Ready(fixture.Request(multipleShares ? @"\\server\share-b\four" : @"\\server\share-a\four"));
        var blocked = fixture.Request(@"\\server\share-c\five");
        Assert.False(blocked.IsCompleted);
        await Ready(fixture.Request(@"\\other\share\one"));
        await Ready(fixture.Request(@"C:\fixture\one"));
        await Ready(fixture.Request(@"C:\fixture\two"));
        await Ready(fixture.Request("relative-one"));
        Assert.Equal(8, fixture.Admission.GetSnapshot().ActiveOperations);
        first.Dispose();
        await Ready(blocked);
        Assert.Equal(8, fixture.Admission.GetSnapshot().ActiveOperations);
    }

    [Fact]
    public async Task LocalRootsAndUnknownPaths_ShareTwoAndOneSlotsRespectively()
    {
        await using var fixture = new Fixture(8);
        var local = await Ready(fixture.Request(@"C:\fixture\one"));
        await Ready(fixture.Request(@"\\?\c:\fixture\two"));
        var blockedLocal = fixture.Request(@"c:\fixture\three");
        Assert.False(blockedLocal.IsCompleted);
        await Ready(fixture.Request(@"D:\fixture\one"));
        await Ready(fixture.Request(@"Z:\mapped\one"));
        var unknown = await Ready(fixture.Request("relative-one"));
        var blockedUnknown = fixture.Request(@"\\");
        Assert.False(blockedUnknown.IsCompleted);
        Assert.Equal(5, fixture.Admission.GetSnapshot().ActiveOperations);
        local.Dispose();
        await Ready(blockedLocal);
        unknown.Dispose();
        await Ready(blockedUnknown);
    }

    [Fact]
    public async Task CancelledQueuesAndHistoricalGroups_AreReclaimed()
    {
        await using var fixture = new Fixture(1);
        var local = await Ready(fixture.Request(@"C:\fixture\one"));
        using var cancellation = new CancellationTokenSource();
        var queued = Enumerable.Range(0, 100).Select(i => fixture.Request($@"\\host-{i}\share\file", cancellation.Token)).ToArray();
        Assert.Equal((1, 100, 201), fixture.Admission.GetSnapshot());
        cancellation.Cancel();
        foreach (var request in queued) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ready(request));
        Assert.Equal((1, 0, 1), fixture.Admission.GetSnapshot());
        local.Dispose();
        for (var i = 0; i < 100; i++)
        {
            (await Ready(fixture.Request($@"\\historical-{i}\share\file"))).Dispose();
            Assert.Equal((0, 0, 0), fixture.Admission.GetSnapshot());
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Request(@"\\cancelled\share\file", cancellation.Token));
        Assert.Equal((0, 0, 0), fixture.Admission.GetSnapshot());
    }

    [Fact]
    public async Task Disposal_CancelsQueueAndActiveLeasesReleaseExactlyOnce()
    {
        await using var fixture = new Fixture(1);
        var active = await Ready(fixture.Request(@"\\host\share\file"));
        var queued = fixture.Request(@"\\other\share\file");
        fixture.Admission.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ready(queued));
        Assert.Equal((1, 0, 2), fixture.Admission.GetSnapshot());
        Assert.Throws<ObjectDisposedException>(() => { _ = fixture.Request(@"C:\fixture\new"); });
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(active.Dispose))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((0, 0, 0), fixture.Admission.GetSnapshot());
    }

    private static Task<IDisposable> Ready(Task<IDisposable> request) => request.WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class Fixture(int limit) : IAsyncDisposable
    {
        internal QueryDiskAdmission Admission { get; } = new(limit);
        private readonly List<Task<IDisposable>> _requests = new();
        internal Task<IDisposable> Request(string path, CancellationToken ct = default)
        {
            var request = Admission.AcquireAsync(path, ct).AsTask();
            _requests.Add(request);
            return request;
        }
        public async ValueTask DisposeAsync()
        {
            Admission.Dispose();
            foreach (var request in _requests)
            {
                try { (await Ready(request)).Dispose(); }
                catch (OperationCanceledException) { }
            }
            Assert.Equal((0, 0, 0), Admission.GetSnapshot());
        }
    }
}
