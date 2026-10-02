namespace LogReader.App.Views;

using System.Windows;
using LogReader.Core;

public partial class FileDisplayNameWindow : Window
{
    public FileDisplayNameWindow(string filePath, string? displayName)
    {
        InitializeComponent();
        PathBox.Text = filePath;
        NameBox.Text = displayName ?? string.Empty;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string? DisplayName { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DisplayName = LogFileDisplayName.Normalize(NameBox.Text);
            DialogResult = true;
        }
        catch (ArgumentException)
        {
            ErrorText.Text = "Enter a single-line name without control characters.";
            NameBox.Focus();
        }
    }
}
