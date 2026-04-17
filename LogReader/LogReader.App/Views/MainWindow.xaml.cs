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
            or nameof(MainViewModel.GroupsPanelWidth)
            or nameof(MainViewModel.SearchPanelHeight))
        {
            ApplyPanelLayout();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDragOverEffects(e.Data);
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = await HandleFileDropAsync(e.Data);
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        e.Handled = await HandleOpenSettingsAsync();
    }

    private void ApplyPanelLayout()
    {
        if (ViewModel == null)
            return;

        GroupsPanelColumn.Width = new GridLength(
            ViewModel.IsGroupsPanelOpen ? ViewModel.GroupsPanelWidth : 29,
            GridUnitType.Pixel);
        SearchPanelRow.Height = new GridLength(ViewModel.SearchPanelHeight, GridUnitType.Pixel);
    }

    private void GroupsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel == null)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            PersistGroupsPanelWidth(GroupsPanelColumn.ActualWidth);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SearchSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel == null)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            PersistSearchPanelHeight(SearchPanelRow.ActualHeight);
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = HandlePreviewShortcut(
            e.Key,
            Keyboard.Modifiers,
            e.OriginalSource as DependencyObject);
    }

    internal DragDropEffects GetDragOverEffects(IDataObject data)
    {
        if (ViewModel?.IsLoadAffectingActionFrozen == true)
            return DragDropEffects.None;

        return TryGetDroppedFiles(data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    internal async Task<bool> HandleFileDropAsync(IDataObject data)
    {
        if (!TryGetDroppedFiles(data, out var files) || ViewModel == null)
            return false;

        if (ViewModel.IsLoadAffectingActionFrozen)
            return false;

        var viewModel = ViewModel;
        await viewModel.RunViewActionAsync(async () =>
        {
            foreach (var file in files)
                await viewModel.OpenFilePathAsync(file);
        });

        return true;
    }

    internal async Task<bool> HandleOpenSettingsAsync()
    {
        if (ViewModel == null)
            return false;

        var viewModel = ViewModel;
        await viewModel.RunViewActionAsync(viewModel.OpenSettingsAsync);
        return true;
    }

    internal void PersistGroupsPanelWidth(double width)
    {
        if (ViewModel == null)
            return;

        ViewModel.RememberGroupsPanelWidth(width);
    }

    internal void PersistSearchPanelHeight(double height)
    {
        if (ViewModel == null)
            return;

        ViewModel.RememberSearchPanelHeight(height);
    }

    internal bool HandlePreviewShortcut(
        Key key,
        ModifierKeys modifiers,
        DependencyObject? originalSource,
        Action? focusSearchWorkspace = null)
    {
        if (modifiers != ModifierKeys.Control)
            return false;

        if (key == Key.F)
        {
            if (ViewModel == null)
                return false;

            Dispatcher.InvokeAsync(
                focusSearchWorkspace ?? SearchWorkspace.FocusActiveTabPrimaryInput,
                System.Windows.Threading.DispatcherPriority.Background);

            return true;
        }

        if (key != Key.Left && key != Key.Right)
            return false;

        if (IsTextInputContext(originalSource) || ViewModel == null)
            return false;

        if (key == Key.Left)
            ViewModel.SelectPreviousTabCommand.Execute(null);
        else
            ViewModel.SelectNextTabCommand.Execute(null);

        return true;
    }

    internal static bool IsTextInputContext(DependencyObject? source)
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

    private static bool TryGetDroppedFiles(IDataObject data, out string[] files)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) &&
            data.GetData(DataFormats.FileDrop) is string[] droppedFiles)
        {
            files = droppedFiles;
            return true;
        }

        files = Array.Empty<string>();
        return false;
    }
}
