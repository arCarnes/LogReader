namespace LogReader.Tests;

using System.Windows;
using LogReader.App.Services;
using LogReader.App.ViewModels;

public class UiAbstractionsTests
{
    [Fact]
    public void SettingsDialogService_AssignsOwner_WhenOwnerProviderReturnsMainWindow()
    {
        WpfTestHost.Run(() =>
        {
            var ownerProvider = new StubWindowOwnerProvider
            {
                Owner = new Window()
            };
            var windowFactory = new StubSettingsDialogWindowFactory();
            var service = new SettingsDialogService(ownerProvider, windowFactory);
            var settingsViewModel = new SettingsViewModel(new StubSettingsRepository());

            var accepted = service.ShowDialog(settingsViewModel);

            Assert.True(accepted);
            Assert.Equal(1, ownerProvider.CallCount);
            Assert.Same(ownerProvider.Owner, windowFactory.Window.Owner);
            Assert.Same(ownerProvider.Owner, windowFactory.Window.OwnerAtShowDialog);
        });
    }

    [Fact]
    public void SettingsDialogService_LeavesOwnerUnset_WhenOwnerProviderReturnsNull()
    {
        var ownerProvider = new StubWindowOwnerProvider();
        var windowFactory = new StubSettingsDialogWindowFactory();
        var service = new SettingsDialogService(ownerProvider, windowFactory);
        var settingsViewModel = new SettingsViewModel(new StubSettingsRepository());

        var accepted = service.ShowDialog(settingsViewModel);

        Assert.True(accepted);
        Assert.Equal(1, ownerProvider.CallCount);
        Assert.Null(windowFactory.Window.Owner);
        Assert.Null(windowFactory.Window.OwnerAtShowDialog);
    }

    [Fact]
    public void SettingsDialogService_AssignsDataContextBeforeShowingDialog()
    {
        var windowFactory = new StubSettingsDialogWindowFactory();
        var service = new SettingsDialogService(new StubWindowOwnerProvider(), windowFactory);
        var settingsViewModel = new SettingsViewModel(new StubSettingsRepository());

        var accepted = service.ShowDialog(settingsViewModel);

        Assert.True(accepted);
        Assert.Equal(1, windowFactory.CreateCallCount);
        Assert.Equal(1, windowFactory.Window.ShowDialogCallCount);
        Assert.Same(settingsViewModel, windowFactory.Window.DataContext);
        Assert.Same(settingsViewModel, windowFactory.Window.DataContextAtShowDialog);
        Assert.Equal(["DataContext", "ShowDialog"], windowFactory.Window.Events);
    }
}
