namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.ViewModels;

public partial class FieldProfilesWindow : Window
{
    public FieldProfilesWindow() => InitializeComponent();
    private void OK_Click(object sender, RoutedEventArgs e)
    {
        FieldsGrid.CommitEdit();
        FieldsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        if (DataContext is FieldProfilesViewModel vm && vm.TryGetProfiles(out _)) DialogResult = true;
    }
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is FieldProfilesViewModel vm) vm.PreviewCommand.Cancel();
        base.OnClosed(e);
    }
}
