namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Infrastructure.Repositories;

public partial class McpHelpWindow : Window, IMcpHelpDialogWindow
{
    private readonly McpHelpPresentation _presentation;
    private readonly IMcpHelpActions _actions;
    internal McpLimitsViewModel Limits { get; }

    internal McpHelpWindow(
        McpHelpPresentation presentation,
        IMcpHelpActions actions,
        ISettingsRepository? settingsRepository = null)
    {
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        Limits = new McpLimitsViewModel(settingsRepository ?? new JsonSettingsRepository());
        InitializeComponent();
        DataContext = presentation;
        LimitsSection.DataContext = Limits;
        Loaded += async (_, _) => await Limits.LoadAsync();
        Closing += (_, e) => e.Cancel = Limits.IsSaving;
    }

    private void CopyServerPath_Click(object sender, RoutedEventArgs e)
        => _actions.CopyServerPath(this, _presentation.ServerExecutablePath);

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
        => _actions.OpenGuide(this, _presentation.GuideUri);

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();
}
