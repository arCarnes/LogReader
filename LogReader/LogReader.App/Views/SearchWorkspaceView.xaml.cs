namespace LogReader.App.Views;

using System.ComponentModel;
using System.Windows.Controls;
using LogReader.App.ViewModels;

public partial class SearchWorkspaceView : UserControl
{
    private MainViewModel? _subscribedViewModel;

    public SearchWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSearchPanelOpen) && ViewModel?.IsSearchPanelOpen == true)
        {
            Dispatcher.InvokeAsync(
                () => SearchBox.Focus(),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
