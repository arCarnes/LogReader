namespace LogReader.App.Views;

using System.Windows;

public partial class ControlsWindow : Window
{
    public ControlsWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
