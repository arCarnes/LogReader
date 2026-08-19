namespace LogReader.Tests;

using System.Windows;
using LogReader.App.Services;
using LogReader.App.ViewModels;

public class ForbiddenUiServiceTests
{
    [Fact]
    public void MessageBoxInvocation_FailsWithoutOpeningUi()
    {
        var service = (IMessageBoxService)ForbiddenUiService.Instance;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.Show("message", "caption", MessageBoxButton.OK, MessageBoxImage.Error));

        Assert.Contains(nameof(IMessageBoxService), exception.Message);
    }

    [Fact]
    public void DialogInvocation_FailsWithoutOpeningUi()
    {
        var service = (ISettingsDialogService)ForbiddenUiService.Instance;
        var viewModel = new SettingsViewModel(new StubSettingsRepository());

        var exception = Assert.Throws<InvalidOperationException>(() => service.ShowDialog(viewModel));

        Assert.Contains(nameof(ISettingsDialogService), exception.Message);
    }

    [Fact]
    public void McpHelpInvocation_FailsWithoutOpeningUi()
    {
        var service = (IMcpHelpDialogService)ForbiddenUiService.Instance;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.ShowDialog(new McpHelpDialogRequest(0)));

        Assert.Contains(nameof(IMcpHelpDialogService), exception.Message);
    }
}
