namespace LogReader.Tests;

using LogReader.App.Services;

public sealed class SingleInstanceCoordinatorTests : IDisposable
{
    public void Dispose()
        => SingleInstanceCoordinator.ReleaseForTests();

    [Fact]
    public void TryAcquire_WhenMutexAvailabilityChanges_ReflectsCurrentSingleInstanceState()
    {
        SingleInstanceCoordinator.ReleaseForTests();

        var availableMutexName = $@"Local\LogReader.SingleInstance.Tests.Available.{Guid.NewGuid():N}";
        var availableCoordinator = new SingleInstanceCoordinator(availableMutexName);
        Assert.True(availableCoordinator.TryAcquire());

        SingleInstanceCoordinator.ReleaseForTests();

        var blockedMutexName = $@"Local\LogReader.SingleInstance.Tests.Blocked.{Guid.NewGuid():N}";
        var competingMutex = new Mutex(initiallyOwned: true, blockedMutexName, out var createdNew);
        Assert.True(createdNew);

        try
        {
            var blockedCoordinator = new SingleInstanceCoordinator(blockedMutexName);
            Assert.False(blockedCoordinator.TryAcquire());
        }
        finally
        {
            competingMutex.ReleaseMutex();
            competingMutex.Dispose();
        }

        var reopenedCoordinator = new SingleInstanceCoordinator(blockedMutexName);
        Assert.True(reopenedCoordinator.TryAcquire());
    }
}
