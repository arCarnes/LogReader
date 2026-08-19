namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.Services;

public partial class McpHelpWindow : Window, IMcpHelpDialogWindow
{
    private readonly McpHelpPresentation _presentation;
    private readonly IMcpHelpActions _actions;

    internal McpHelpWindow(
        McpHelpPresentation presentation,
        IMcpHelpActions actions)
    {
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        InitializeComponent();
        DataContext = presentation;
    }

    private void CopyServerPath_Click(object sender, RoutedEventArgs e)
        => _actions.CopyServerPath(this, _presentation.ServerExecutablePath);

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
        => _actions.OpenGuide(this, _presentation.GuideUri);

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();
}
