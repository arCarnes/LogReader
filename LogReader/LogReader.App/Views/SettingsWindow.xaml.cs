namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HighlightRuleViewModel rule) return;
        var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };

        try { dialog.Color = System.Drawing.ColorTranslator.FromHtml(rule.Color); } catch { }
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            rule.Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            if (DataContext is SettingsViewModel acceptedSettingsViewModel)
                acceptedSettingsViewModel.RememberHighlightColor(rule.Color);
        }
    }

    private void RecentColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color } btn)
            return;

        var rule = FindAncestor<ItemsControl>(btn, itemsControl => itemsControl.Tag is HighlightRuleViewModel)?.Tag as HighlightRuleViewModel;
        if (rule == null)
            return;

        rule.Color = color;
        if (DataContext is SettingsViewModel settingsViewModel)
            settingsViewModel.RememberHighlightColor(color);
    }

    private static T? FindAncestor<T>(DependencyObject source, Func<T, bool> predicate)
        where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(source);
        while (current != null)
        {
            if (current is T typed && predicate(typed))
                return typed;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
