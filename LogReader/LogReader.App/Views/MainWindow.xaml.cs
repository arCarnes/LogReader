namespace LogReader.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LogReader.App.Services;
using LogReader.App.ViewModels;

public partial class MainWindow : Window
{
    private MainViewModel? _subscribedViewModel;
    private Application? _subscribedApplication;
    private bool _isApplicationActive = true;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        StateChanged += MainWindow_StateChanged;
        DataContextChanged += MainWindow_DataContextChanged;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeApplicationEvents();
        ApplyPanelLayout();
        PublishTailingActivityState();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        UnsubscribeApplicationEvents();
        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _subscribedViewModel = null;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
        => HandleWindowStateChanged();

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;

        ApplyPanelLayout();
        PublishTailingActivityState();
    }

    private void SubscribeApplicationEvents()
    {
        if (_subscribedApplication != null)
            return;

        _subscribedApplication = Application.Current;
        if (_subscribedApplication == null)
            return;

        _subscribedApplication.Activated += Application_Activated;
        _subscribedApplication.Deactivated += Application_Deactivated;
    }

    private void UnsubscribeApplicationEvents()
    {
        if (_subscribedApplication == null)
            return;

        _subscribedApplication.Activated -= Application_Activated;
        _subscribedApplication.Deactivated -= Application_Deactivated;
        _subscribedApplication = null;
    }

    private void Application_Activated(object? sender, EventArgs e)
        => HandleApplicationActivated();

    private void Application_Deactivated(object? sender, EventArgs e)
        => HandleApplicationDeactivated();

    internal void HandleApplicationActivated()
    {
        _isApplicationActive = true;
        PublishTailingActivityState();
    }

    internal void HandleApplicationDeactivated()
    {
        _isApplicationActive = false;
        PublishTailingActivityState();
    }

    internal void HandleWindowStateChanged()
        => PublishTailingActivityState();

    private void PublishTailingActivityState()
    {
        var state = WindowState == WindowState.Minimized
            ? TailingActivityState.Minimized
            : _isApplicationActive
                ? TailingActivityState.RestoredForeground
                : TailingActivityState.RestoredInactive;
        ViewModel?.SetTailingActivityState(state);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsGroupsPanelOpen)
            or nameof(MainViewModel.IsSearchPanelOpen)
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
            !ViewModel.IsGroupsPanelOpen
                ? MainViewModel.CollapsedGroupsPanelWidth
                : ViewModel.GroupsPanelWidth,
            GridUnitType.Pixel);
        SearchPanelRow.Height = new GridLength(
            !ViewModel.IsSearchPanelOpen
                ? MainViewModel.CollapsedSearchPanelHeight
                : ViewModel.SearchPanelHeight,
            GridUnitType.Pixel);
    }

    private void GroupsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel == null)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            HandleGroupsPanelDragCompleted(GroupsPanelColumn.ActualWidth);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SearchSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel == null)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            HandleSearchPanelDragCompleted(SearchPanelRow.ActualHeight);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ShowControls_Click(object sender, RoutedEventArgs e)
    {
        var controlsWindow = new ControlsWindow
        {
            Owner = this,
            DataContext = ViewModel
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
            await viewModel.OpenFilePathsAsync(files);
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

    internal void HandleGroupsPanelDragCompleted(double width)
    {
        if (ViewModel == null)
            return;

        if (width <= MainViewModel.GroupsPanelSnapThreshold)
        {
            if (ViewModel.IsGroupsPanelOpen)
                ViewModel.ToggleGroupsPanelCommand.Execute(null);

            return;
        }

        ViewModel.RememberGroupsPanelWidth(width);
    }

    internal void PersistSearchPanelHeight(double height)
    {
        if (ViewModel == null)
            return;

        ViewModel.RememberSearchPanelHeight(height);
    }

    internal void HandleSearchPanelDragCompleted(double height)
    {
        if (ViewModel == null)
            return;

        if (height <= MainViewModel.SearchPanelSnapThreshold)
        {
            if (ViewModel.IsSearchPanelOpen)
                ViewModel.ToggleSearchPanelCommand.Execute(null);

            return;
        }

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

            if (!ViewModel.IsSearchPanelOpen)
                ViewModel.ToggleSearchPanelCommand.Execute(null);

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
