namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using LogReader.App.ViewModels;

internal sealed partial class ViewLibraryWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ListBox _views = new() { DisplayMemberPath = nameof(ViewChoice.ManagementName), Height = 120 };
    private readonly TextBox _name = new() { MinWidth = 220, Margin = new Thickness(4) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private CancellationTokenSource? _operationCancellation;
    private bool _isRunning;
    private Button? _renameButton;
    private Button? _deleteButton;

    public ViewLibraryWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Views and Sources";
        Width = 820; Height = 780; MinWidth = 720; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Closing += (_, e) => { if (_isRunning) { _operationCancellation?.Cancel(); e.Cancel = true; } };
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        var content = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(new TextBlock { Text = "My Views and linked views", FontSize = 18, Margin = new Thickness(4) });
        _views.ItemsSource = viewModel.ViewChoices;
        _views.SelectionChanged += (_, _) =>
        {
            if (_views.SelectedItem is ViewChoice choice) _name.Text = choice.Name;
            UpdateActionState();
        };
        content.Children.Add(_views);
        content.Children.Add(new TextBlock { Text = "View name", Margin = new Thickness(4) });
        content.Children.Add(_name);
        content.Children.Add(_actions);
        AddAction("New View", () => RunAsync(() => _viewModel.RunViewLibraryActionAsync(l => l.CreateAsync(_name.Text))));
        AddAction("Activate", () => SelectedAsync(choice => _viewModel.RunViewLibraryActionAsync(l => l.ActivateAsync(choice.Identity))));
        AddAction("Copy active to My Views", () => RunAsync(() => _viewModel.RunViewLibraryActionAsync(l => l.CreateAsync(_name.Text, copyActive: true))));
        _renameButton = AddAction("Rename", () => SelectedAsync(choice => choice.Identity.IsLocal
            ? _viewModel.RunViewLibraryActionAsync(l => l.RenameAsync(choice.Identity.ViewId, _name.Text), false)
            : throw new InvalidOperationException("Copy linked views to My Views before editing.")));
        _deleteButton = AddAction("Delete", () => SelectedAsync(choice => choice.Identity.IsLocal
            ? _viewModel.RunViewLibraryActionAsync(l => l.DeleteAsync(choice.Identity.ViewId), false)
            : throw new InvalidOperationException("Use Remove Source to remove linked views.")));
        AddSources(content);
        _views.SelectedItem = viewModel.ActiveViewChoice;
        UpdateActionState();
    }

    private Button AddAction(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        button.Click += async (_, _) => await action();
        _actions.Children.Add(button);
        return button;
    }

    private void UpdateActionState()
    {
        var choice = _views.SelectedItem as ViewChoice;
        if (_renameButton != null) _renameButton.IsEnabled = choice?.Identity.IsLocal == true;
        if (_deleteButton != null) _deleteButton.IsEnabled = choice?.Identity.IsLocal == true &&
            choice.Identity != _viewModel.ViewLibrary!.Library!.Active && _viewModel.ViewLibrary.Library.LocalViews.Count > 1;
    }

    private Task SelectedAsync(Func<ViewChoice, Task> action) => RunAsync(async () =>
    {
        if (_views.SelectedItem is not ViewChoice choice) throw new InvalidOperationException("Select a view first.");
        await action(choice);
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (_isRunning) return;
        _isRunning = true;
        _operationCancellation = new CancellationTokenSource();
        _actions.IsEnabled = false;
        SetSourcesBusy(true);
        _status.Text = "Working…";
        try { await action(); _status.Text = "Ready"; }
        catch (OperationCanceledException) { _status.Text = "Cancelled. The accepted views were preserved."; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally
        {
            _isRunning = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _actions.IsEnabled = true;
            SetSourcesBusy(false);
            RefreshSources();
            _views.SelectedItem = _viewModel.ActiveViewChoice;
            UpdateActionState();
        }
    }

    partial void AddSources(StackPanel content);
    partial void RefreshSources();
    partial void SetSourcesBusy(bool value);
}
