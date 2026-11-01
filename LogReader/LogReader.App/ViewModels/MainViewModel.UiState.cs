namespace LogReader.App.ViewModels;

using System.Windows;
using LogReader.App.Services;
using LogReader.Core.Interfaces;

public partial class MainViewModel
{
    private UiStatePersistenceService _uiStatePersistence = null!;
    private IUiDispatcher _uiStateDispatcher = null!;

    private void ConfigureUiState(IUiStateRepository repository, IUiDispatcher dispatcher)
    {
        _uiStateDispatcher = dispatcher;
        _uiStatePersistence = new UiStatePersistenceService(repository, ReportUiStateFailure);
        _dashboardWorkspace.ExpansionStateChanged += DashboardExpansionStateChanged;
    }

    private void DashboardExpansionStateChanged()
        => _uiStatePersistence.Queue(_dashboardWorkspace.CaptureExpansionState());

    private void ReportUiStateFailure(Exception exception)
    {
        _ = _uiStateDispatcher.InvokeAsync(() => _messageBoxService.Show(
            "WeezTail couldn't restore or save folder and dashboard expansion. You can continue using the app." +
            Environment.NewLine + Environment.NewLine + exception.Message,
            "UI state", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    internal Task FlushUiStateAsync()
        => _uiStatePersistence.FlushAsync(_dashboardWorkspace.CaptureExpansionState(), TimeSpan.FromSeconds(2));
}
