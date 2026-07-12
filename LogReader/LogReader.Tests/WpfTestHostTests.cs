namespace LogReader.Tests;

using System.Windows;
using System.Windows.Threading;

public class WpfTestHostTests
{
    [Fact]
    public async Task RunAsync_DispatcherException_IsReturnedToTheTestRunner()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WpfTestHost.RunAsync(async () =>
        {
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                () => throw new InvalidOperationException("dispatcher failure"));
            await WpfTestHost.FlushAsync();
        }));

        Assert.Equal("dispatcher failure", exception.Message);
    }

    [Fact]
    public async Task RunAsync_ActionFailure_ClosesWindowsAndAllowsNextApplication()
    {
        var windowClosed = false;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WpfTestHost.RunAsync(() =>
        {
            var window = new Window();
            window.Closed += (_, _) => windowClosed = true;
            WpfTestHost.ShowHidden(window);
            throw new InvalidOperationException("action failure");
        }));

        Assert.Equal("action failure", exception.Message);
        Assert.True(windowClosed);

        await WpfTestHost.RunAsync(() =>
        {
            Assert.NotNull(Application.Current);
            Assert.Empty(Application.Current.Windows.OfType<Window>());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ShowHidden_RealizesAnInvisibleOffscreenWindow()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var window = new Window { Width = 320, Height = 180 };
            WpfTestHost.ShowHidden(window);

            Assert.True(window.IsVisible);
            Assert.Equal(1, window.Opacity);
            Assert.False(window.ShowInTaskbar);
            Assert.True(window.Left < SystemParameters.VirtualScreenLeft);
            Assert.True(window.Top < SystemParameters.VirtualScreenTop);
            return Task.CompletedTask;
        });
    }
}
