namespace LogReader.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LogReader.App.ViewModels;

public partial class MainWindow : Window
{
    private const double CollapsedRailWidth = 29;

    private MainViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyPanelLayout();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;

            ApplyPanelLayout();
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsGroupsPanelOpen)
            or nameof(MainViewModel.IsSearchPanelOpen)
            or nameof(MainViewModel.GroupsPanelWidth)
            or nameof(MainViewModel.SearchPanelWidth))
        {
            ApplyPanelLayout();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || ViewModel == null)
            return;

        var viewModel = ViewModel;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await viewModel.RunViewActionAsync(async () =>
        {
            foreach (var file in files)
                await viewModel.OpenFilePathAsync(file);
        });
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        var viewModel = ViewModel;
        await viewModel.RunViewActionAsync(() => viewModel.OpenSettingsAsync(this));
    }

    private void ApplyPanelLayout()
    {
        if (ViewModel == null)
            return;

        GroupsPanelColumn.Width = new GridLength(
            ViewModel.IsGroupsPanelOpen ? ViewModel.GroupsPanelWidth : CollapsedRailWidth,
            GridUnitType.Pixel);
        SearchPanelColumn.Width = new GridLength(
            ViewModel.IsSearchPanelOpen ? ViewModel.SearchPanelWidth : CollapsedRailWidth,
            GridUnitType.Pixel);
    }

    private void GroupsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel == null)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            if (ViewModel == null)
                return;

            if (GroupsPanelColumn.ActualWidth <= CollapsedRailWidth + 0.5)
            {
                if (ViewModel.IsGroupsPanelOpen)
                    ViewModel.ToggleGroupsPanelCommand.Execute(null);

                return;
            }

            ViewModel.RememberGroupsPanelWidth(GroupsPanelColumn.ActualWidth);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SearchSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel == null)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            if (ViewModel == null)
                return;

            if (SearchPanelColumn.ActualWidth <= CollapsedRailWidth + 0.5)
            {
                if (ViewModel.IsSearchPanelOpen)
                    ViewModel.ToggleSearchPanelCommand.Execute(null);

                return;
            }

            ViewModel.RememberSearchPanelWidth(SearchPanelColumn.ActualWidth);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ShowControls_Click(object sender, RoutedEventArgs e)
    {
        var controlsWindow = new ControlsWindow
        {
            Owner = this
        };

        controlsWindow.ShowDialog();
    }

    private void GroupsSplitter_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.IsGroupsPanelOpen == true)
            ViewModel.ToggleGroupsPanelCommand.Execute(null);
    }

    private void SearchSplitter_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.IsSearchPanelOpen == true)
            ViewModel.ToggleSearchPanelCommand.Execute(null);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        if (e.Key == Key.F)
        {
            if (ViewModel == null)
                return;

            if (!ViewModel.IsSearchPanelOpen)
                ViewModel.ToggleSearchPanelCommand.Execute(null);

            Dispatcher.InvokeAsync(
                SearchWorkspace.FocusActiveTabPrimaryInput,
                System.Windows.Threading.DispatcherPriority.Background);

            e.Handled = true;
            return;
        }

        if (e.Key != Key.Left && e.Key != Key.Right)
            return;

        if (IsTextInputContext(e.OriginalSource as DependencyObject))
            return;

        if (ViewModel == null)
            return;

        if (e.Key == Key.Left)
            ViewModel.SelectPreviousTabCommand.Execute(null);
        else
            ViewModel.SelectNextTabCommand.Execute(null);

        e.Handled = true;
    }

    private static bool IsTextInputContext(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is TextBoxBase || current is PasswordBox || current is ComboBox { IsEditable: true })
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
