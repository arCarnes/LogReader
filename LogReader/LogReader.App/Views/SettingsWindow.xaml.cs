namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LogReader.App.ViewModels;

public partial class SettingsWindow : Window
{
    private HighlightRuleViewModel? _activeColorRule;
    private bool _isTextColorTarget;

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

    private void ManageFieldProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings) return;
        var editor = new FieldProfilesViewModel(settings.FieldProfiles);
        var window = new FieldProfilesWindow { Owner = this, DataContext = editor };
        if (window.ShowDialog() == true && editor.TryGetProfiles(out var profiles))
            settings.FieldProfiles = profiles;
    }

    private void OpenColorPalette_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not SettingsViewModel)
            return;

        _activeColorRule = button.Tag as HighlightRuleViewModel;
        _isTextColorTarget = _activeColorRule != null && Equals(button.CommandParameter, "Text");
        ColorPalettePopup.IsOpen = false;
        ColorPalettePopup.DataContext = DataContext;
        ColorPalettePopup.PlacementTarget = button;
        ColorPalettePopup.IsOpen = true;
    }

    private void PickCustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settingsViewModel)
            return;

        ColorPalettePopup.IsOpen = false;
        var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        var currentColor = _activeColorRule is { } rule
            ? _isTextColorTarget ? rule.TextColor : rule.Color
            : settingsViewModel.SearchMatchHighlightColor;

        try { dialog.Color = System.Drawing.ColorTranslator.FromHtml(currentColor); } catch { }
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ApplyColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}", settingsViewModel);
    }

    private void RecentColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color } || DataContext is not SettingsViewModel settingsViewModel)
            return;

        ApplyColor(color, settingsViewModel);
        ColorPalettePopup.IsOpen = false;
    }

    private void ApplyColor(string color, SettingsViewModel settingsViewModel)
    {
        if (_activeColorRule is { } rule)
        {
            if (_isTextColorTarget)
                rule.TextColor = color;
            else
                rule.Color = color;
        }
        else
            settingsViewModel.SearchMatchHighlightColor = color;

        settingsViewModel.RememberHighlightColor(color);
    }

    private void ColorPalettePopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        ColorPalettePopup.IsOpen = false;
        e.Handled = true;
    }
}
