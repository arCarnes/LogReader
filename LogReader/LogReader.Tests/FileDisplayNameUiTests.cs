namespace LogReader.Tests;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LogReader.App.Controls;
using LogReader.App.ViewModels;
using LogReader.App.Services;
using LogReader.App.Views;
using LogReader.Core.Models;

public sealed class FileDisplayNameUiTests
{
    [Theory]
    [InlineData(AppTheme.Default)]
    [InlineData(AppTheme.EasyReading)]
    [InlineData(AppTheme.Dark)]
    public async Task DisplayName_DialogSavesNormalizedNameAndRendersWithTheme(AppTheme theme)
    {
        await WpfTestHost.RunAsync(() =>
        {
            new WpfLogAppearanceService().Apply(new AppSettings { Theme = theme });
            var dialog = new FileDisplayNameWindow(@"C:\logs\production\app.log", "Production API")
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false
            };
            dialog.Loaded += (_, _) =>
            {
                var box = Assert.IsType<TextBox>(dialog.FindName("NameBox"));
                Assert.Equal("Production API", box.Text);
                Assert.Equal(@"C:\logs\production\app.log", Assert.IsType<TextBox>(dialog.FindName("PathBox")).Text);
                Assert.True(Assert.IsType<TextBox>(dialog.FindName("PathBox")).IsReadOnly);
                dialog.UpdateLayout();
                var root = Assert.IsType<StackPanel>(dialog.Content);
                Assert.True(root.ActualHeight > 0);
                Assert.True(root.ActualWidth > 0);
                var directory = Environment.GetEnvironmentVariable("WEEZTAIL_DISPLAY_NAME_SMOKE_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth),
                        (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(dialog);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"display-name-{theme}.png"));
                    encoder.Save(stream);
                }
                box.Text = "  Production API  ";
                var buttons = root.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToList();
                Assert.True(buttons[0].IsDefault);
                Assert.True(buttons[1].IsCancel);
                buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            Assert.True(dialog.ShowDialog());
            Assert.Equal("Production API", dialog.DisplayName);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DisplayName_DialogCancelDiscardsInputAndBlankSaveResets()
    {
        await WpfTestHost.RunAsync(() =>
        {
            foreach (var save in new[] { false, true })
            {
                var dialog = new FileDisplayNameWindow(@"C:\logs\app.log", "API")
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false
                };
                dialog.Loaded += (_, _) =>
                {
                    Assert.IsType<TextBox>(dialog.FindName("NameBox")).Text = save ? " " : "Changed";
                    if (save)
                    {
                        var root = Assert.IsType<StackPanel>(dialog.Content);
                        root.Children.OfType<StackPanel>().Single().Children.OfType<Button>().First()
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else
                        dialog.DialogResult = false;
                };
                Assert.Equal(save, dialog.ShowDialog());
                Assert.Null(dialog.DisplayName);
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DisplayName_ContextMenuResetTargetsClickedMemberAndHonorsLoadingState()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var repo = new StubLogFileRepository();
            var first = new LogFileEntry { Id = "first", FilePath = @"C:\logs\api.log", DisplayName = "API" };
            var second = new LogFileEntry { Id = "second", FilePath = @"C:\logs\worker.log", DisplayName = "Worker" };
            await repo.AddAsync(first);
            await repo.AddAsync(second);
            using var vm = TestMainViewModelFactory.Create(repo, new StubLogGroupRepository(), new StubSettingsRepository(),
                new StubLogReaderService(), new StubSearchService(), new StubFileTailService(), new StubEncodingDetectionService());
            var group = new LogGroupViewModel(new LogGroup { Id = "dashboard", Name = "Production", FileIds = [first.Id, second.Id] },
                _ => Task.CompletedTask) { IsExpanded = true };
            group.MemberFiles.Add(new GroupFileMemberViewModel(first.Id, "api.log", first.FilePath, false, customDisplayName: first.DisplayName));
            group.MemberFiles.Add(new GroupFileMemberViewModel(second.Id, "worker.log", second.FilePath, false, customDisplayName: second.DisplayName));
            vm.Groups.Add(group);
            vm.IsGroupsPanelOpen = true;
            foreach (var member in group.MemberFiles)
                member.IsBatchSelected = true;
            var tree = new DashboardTreeView { DataContext = vm };
            var window = new Window { Style = new Style(typeof(Window)), Width = 460, Height = 500, Content = tree };
            WpfTestHost.ShowHidden(window);
            await WpfTestHost.FlushAsync();
            var row = Descendants<Border>(tree).Single(border => border.DataContext is GroupFileMemberViewModel member
                && member.FileId == second.Id && border.ContextMenu != null);
            var menu = row.ContextMenu!;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            await WpfTestHost.FlushAsync();
            Assert.Same(group.MemberFiles[1], menu.DataContext);
            var reset = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Reset Display Name"));
            var set = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Set Display Name..."));
            Assert.True(reset.IsEnabled);
            Assert.True(set.IsEnabled);
            vm.IsDashboardLoading = true;
            await WpfTestHost.FlushAsync();
            Assert.False(reset.IsEnabled);
            Assert.False(set.IsEnabled);
            vm.IsDashboardLoading = false;
            await WpfTestHost.FlushAsync();
            reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WpfTestHost.FlushAsync();
            Assert.Equal("API", first.DisplayName);
            Assert.Null(second.DisplayName);
            Assert.Equal("API", group.MemberFiles[0].DisplayName);
            Assert.Equal("worker.log", group.MemberFiles[1].DisplayName);
            Assert.All(group.MemberFiles, member => Assert.True(member.IsBatchSelected));
            Assert.False(reset.IsEnabled);
            menu.IsOpen = false;
            window.Close();
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    [Fact]
    public async Task DisplayName_ControlKeepsCustomLabelSeparateFromFullPath()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var label = new DashboardPathTextBlock
            {
                FilePath = @"C:\logs\app.log", FileName = "app.log", ShowFullPath = true,
                CustomDisplayName = "Production API"
            };
            var window = new Window { Style = new Style(typeof(Window)), Width = 800, Height = 100, Content = label };
            WpfTestHost.ShowHidden(window);
            await WpfTestHost.FlushAsync();
            Assert.Equal("Production API", label.Text);
            label.CustomDisplayName = null;
            Assert.Equal(label.FilePath, label.Text);
            window.Close();
        });
    }
}
