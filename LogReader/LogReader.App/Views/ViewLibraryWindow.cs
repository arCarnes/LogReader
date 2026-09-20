namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using LogReader.App.ViewModels;

internal sealed partial class ViewLibraryWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ListBox _views = new() { DisplayMemberPath = nameof(ViewChoice.DisplayName), MinHeight = 150 };
    private readonly TextBox _name = new() { MinWidth = 220, Margin = new Thickness(4) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };

    public ViewLibraryWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Views and Sources";
        Width = 760; Height = 640; MinWidth = 650; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(new TextBlock { Text = "My Views and linked views", FontSize = 18, Margin = new Thickness(4) });
        _views.ItemsSource = viewModel.ViewChoices;
        _views.SelectionChanged += (_, _) =>
        {
            if (_views.SelectedItem is ViewChoice choice) _name.Text = choice.Name;
        };
        content.Children.Add(_views);
        content.Children.Add(new TextBlock { Text = "View name", Margin = new Thickness(4) });
        content.Children.Add(_name);
        content.Children.Add(_actions);
        AddAction("New View", () => RunAsync(() => _viewModel.RunViewLibraryActionAsync(l => l.CreateAsync(_name.Text))));
        AddAction("Activate", () => SelectedAsync(choice => _viewModel.RunViewLibraryActionAsync(l => l.ActivateAsync(choice.Identity))));
        AddAction("Copy active to My Views", () => RunAsync(() => _viewModel.RunViewLibraryActionAsync(l => l.CreateAsync(_name.Text, copyActive: true))));
        AddAction("Rename", () => SelectedAsync(choice => choice.Identity.IsLocal
            ? _viewModel.RunViewLibraryActionAsync(l => l.RenameAsync(choice.Identity.ViewId, _name.Text), false)
            : throw new InvalidOperationException("Copy linked views to My Views before editing.")));
        AddAction("Delete", () => SelectedAsync(choice => choice.Identity.IsLocal
            ? _viewModel.RunViewLibraryActionAsync(l => l.DeleteAsync(choice.Identity.ViewId), false)
            : throw new InvalidOperationException("Use Remove Source to remove linked views.")));
        AddSources(content);
        content.Children.Add(_status);
        _views.SelectedItem = viewModel.ActiveViewChoice;
    }

    private void AddAction(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        button.Click += async (_, _) => await action();
        _actions.Children.Add(button);
    }

    private Task SelectedAsync(Func<ViewChoice, Task> action) => RunAsync(async () =>
    {
        if (_views.SelectedItem is not ViewChoice choice) throw new InvalidOperationException("Select a view first.");
        await action(choice);
    });

    private async Task RunAsync(Func<Task> action)
    {
        _actions.IsEnabled = false;
        _status.Text = "Working…";
        try { await action(); _status.Text = "Ready"; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _actions.IsEnabled = true; RefreshSources(); }
    }

    partial void AddSources(StackPanel content);
    partial void RefreshSources();
}
