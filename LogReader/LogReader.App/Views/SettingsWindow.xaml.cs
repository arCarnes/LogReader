namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using LogReader.App.ViewModels;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { HasValidationErrors: true })
        {
            MessageBox.Show(
                this,
                "One or more date rolling patterns have validation errors. Please fix them before saving.",
                "Validation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void PresetColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex && btn.DataContext is HighlightRuleViewModel rule)
            rule.Color = hex;
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HighlightRuleViewModel rule) return;
        var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try { dialog.Color = System.Drawing.ColorTranslator.FromHtml(rule.Color); } catch { }
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            rule.Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }
}
