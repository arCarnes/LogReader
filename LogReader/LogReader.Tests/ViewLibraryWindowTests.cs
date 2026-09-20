namespace LogReader.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core;
using LogReader.Core.Models;

public sealed class ViewLibraryWindowTests
{
    [Fact]
    public async Task ProductionComposition_ShowsLocalAndLinkedViews_AndSwitchesWithoutLosingDefinitions()
    {
        var root = Path.Combine(Path.GetTempPath(), "WeezTailViewUi-" + Guid.NewGuid().ToString("N"));
        using var scope = AppPaths.BeginTestScope(rootPath: root);
        try
        {
            await WpfTestHost.RunAsync(async () =>
            {
                var composition = new AppCompositionBuilder().Build(false);
                using var vm = composition.MainViewModel;
                try
                {
                    await vm.InitializeAsync();
                    var initial = vm.ViewLibrary!.Library!.Active;
                    await vm.RunViewLibraryActionAsync(l => l.CreateAsync("Investigation"));
                    Assert.Equal(2, vm.ViewChoices.Count);
                    var main = new MainWindow { DataContext = vm };
                    WpfTestHost.ShowHidden(main);
                    await WpfTestHost.FlushAsync();
                    var selector = Descendants<ComboBox>(main).Single(c => ReferenceEquals(c.ItemsSource, vm.ViewChoices));
                    selector.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, vm.ViewChoices.Single(v => v.Identity == initial));
                    for (var attempt = 0; attempt < 100 && vm.IsViewOperationRunning; attempt++)
                    {
                        await Task.Delay(10);
                        await WpfTestHost.FlushAsync();
                    }
                    Assert.False(vm.IsViewOperationRunning);
                    Assert.Equal("Default View", vm.ActiveViewChoice!.Name);
                    var source = new ViewSourceRegistration { Name = "Operations", Location = "offline" };
                    await vm.RunViewLibraryActionAsync(l => l.AcceptSourceAsync(source, new ViewSourceSnapshot
                    {
                        Name = "Operations", Revision = "abc", Views = [new SavedView { Id = "production", Name = "Production" }]
                    }), false);
                    await vm.RunViewLibraryActionAsync(l => l.ActivateAsync(new(source.Id, "production")));
                    await WpfTestHost.FlushAsync();
                    Assert.False(vm.CanEditCurrentView);
                    Assert.Equal(vm.ActiveViewChoice, selector.SelectedItem);
                    var window = new ViewLibraryWindow(vm);
                    WpfTestHost.ShowHidden(window);
                    await WpfTestHost.FlushAsync();
                    var buttons = Descendants<Button>(window).ToList();
                    Assert.False(buttons.Single(b => Equals(b.Content, "Delete")).IsEnabled);
                    Assert.False(buttons.Single(b => Equals(b.Content, "Rename")).IsEnabled);
                    Assert.Contains(buttons, b => Equals(b.Content, "Add Folder"));
                    Assert.Contains(buttons, b => Equals(b.Content, "Add Git Source"));
                    Assert.DoesNotContain(buttons, b => b.Content?.ToString()?.Contains("Push", StringComparison.OrdinalIgnoreCase) == true);
                    var artifacts = Environment.GetEnvironmentVariable("WEEZTAIL_VIEW_SCREENSHOTS");
                    if (!string.IsNullOrWhiteSpace(artifacts))
                    {
                        Directory.CreateDirectory(artifacts);
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(artifacts, "view-library.png"));
                        encoder.Save(stream);
                    }
                    window.Close();
                    await vm.RunViewLibraryActionAsync(l => l.CreateAsync("Personal production", true));
                    Assert.True(vm.CanEditCurrentView);
                    Assert.Equal(4, vm.ViewChoices.Count);
                    main.Close();
                }
                finally { (composition.TailService as IDisposable)?.Dispose(); }
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
