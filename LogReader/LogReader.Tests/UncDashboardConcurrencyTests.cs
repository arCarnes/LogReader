namespace LogReader.Tests;

using LogReader.App.Services;

public sealed class UncDashboardConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundUnc_FourSlotsPreserveTopologyPreviewPriorityAndCancellation(bool multipleShares)
    {
        using var scheduler = new DashboardLoadScheduler();
        scheduler.SetForeground("scope");
        using var cancellation = new CancellationTokenSource();
        var requests = new List<Task<DashboardLoadScheduler.Lease>>();
        string[] paths = [@"\\SERVER\share-a\one", @"\\server\SHARE-A\two",
            multipleShares ? @"\\?\UNC\server\share-b\three" : @"\\?\UNC\server\share-a\three",
            multipleShares ? @"\\server\share-b\four" : @"\\server\share-a\four"];
        try
        {
            var active = new List<DashboardLoadScheduler.Lease>();
            foreach (var path in paths) active.Add(await Request(path).WaitAsync(TimeSpan.FromSeconds(5)));
            var index = Request(@"\\server\share-c\queued");
            Assert.False(index.IsCompleted); // A third share cannot multiply the host's four slots.
            await Request(@"\\other-host\share\file").WaitAsync(TimeSpan.FromSeconds(5));
            await Request(@"C:\fixture\one").WaitAsync(TimeSpan.FromSeconds(5));
            await Request(@"C:\fixture\two").WaitAsync(TimeSpan.FromSeconds(5));
            await Request("relative-one").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Request(@"C:\fixture\three").IsCompleted);
            Assert.False(Request("relative-two").IsCompleted);

            var preview = Request(@"\\server\SHARE-A\preview", preview: true);
            Assert.False(preview.IsCompleted);
            Assert.All(active, lease => Assert.True(lease.ShouldYield()));
            active[0].Dispose();
            var previewLease = await preview.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(index.IsCompleted); // Foreground preview reserves the freed slot.
            previewLease.Dispose();
            var indexLease = await index.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(indexLease.ShouldYield());
        }
        finally
        {
            cancellation.Cancel();
            scheduler.Dispose();
            foreach (var request in requests)
            {
                try { (await request.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }

        Task<DashboardLoadScheduler.Lease> Request(string path, bool preview = false)
        {
            var request = scheduler.AcquireAsync(path, () => ["scope"], preview, cancellation.Token);
            requests.Add(request);
            return request;
        }
    }
}
