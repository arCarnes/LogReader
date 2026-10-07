namespace LogReader.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class McpLimitsTests
{
    [Fact]
    public async Task EditorDisablesActionsDuringSaveAndAfterLoadFailure()
    {
        var repo = new Repository { FailLoad = true };
        var vm = new McpLimitsViewModel(repo);
        await vm.LoadAsync();
        Assert.False(vm.CanEdit);
        Assert.Contains("could not be loaded", vm.Status);
        repo.FailLoad = false;
        await vm.LoadAsync();
        repo.SaveCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsSaving);
        Assert.False(vm.CanEdit);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.RestoreDefaultsCommand.CanExecute(null));
        repo.SaveCompletion.SetResult();
        await saving;
        Assert.True(vm.CanEdit);
        Assert.Equal(1, repo.Saves);
    }

    [Fact]
    public async Task EditorPreservesFreshSettingsAndKeepsDraftOnFailure()
    {
        var repo = new Repository();
        var vm = new McpLimitsViewModel(repo);
        Assert.False(vm.CanSave);
        await vm.LoadAsync();
        vm.SelectedProfile = McpLimitsViewModel.Profiles[2];
        repo.Settings.LogFontSize = 18;
        repo.FailSave = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("could not be saved", vm.Status);
        Assert.Equal(32, vm.MaximumSessions);
        Assert.True(vm.CanSave);
        repo.FailSave = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(32, repo.Settings.McpContinuationLimits!.MaximumSessions);
        Assert.Equal(18, repo.Settings.LogFontSize);
        Assert.Contains("Restart", vm.Status);
        vm.RestoreDefaultsCommand.Execute(null);
        Assert.Equal(16, vm.MaximumSessions);
        Assert.Equal(32, repo.Settings.McpContinuationLimits.MaximumSessions);
    }

    [Theory]
    [InlineData(0, 8, 32, 128)]
    [InlineData(1, 16, 64, 256)]
    [InlineData(2, 32, 128, 512)]
    public async Task ProfileLoadsAndSavesExactLimits(int index, int sessions, int query, int total)
    {
        var limits = new McpContinuationLimits
            { MaximumSessions = sessions, MemoryPerQueryMiB = query, TotalMemoryMiB = total };
        Assert.Null(limits.Validate());
        var repo = new Repository { Settings = new AppSettings { McpContinuationLimits = limits } };
        var vm = new McpLimitsViewModel(repo);
        await vm.LoadAsync();
        Assert.Same(McpLimitsViewModel.Profiles[index], vm.SelectedProfile);
        Assert.Null(vm.ProfileNotice);
        Assert.Equal(sessions, vm.MaximumSessions);
        Assert.Equal(query, vm.MemoryPerQueryMiB);
        Assert.Equal(total, vm.TotalMemoryMiB);
        vm.SelectedProfile = McpLimitsViewModel.Profiles[(index + 1) % 3];
        vm.SelectedProfile = McpLimitsViewModel.Profiles[index];
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(limits, repo.Settings.McpContinuationLimits);
        Assert.Equal(1, repo.Saves);
    }

    [Fact]
    public async Task MissingLimitsSelectStandardWithoutWriting()
    {
        var repo = new Repository();
        var vm = new McpLimitsViewModel(repo);
        await vm.LoadAsync();
        Assert.Same(McpLimitsViewModel.Profiles[1], vm.SelectedProfile);
        Assert.Null(repo.Settings.McpContinuationLimits);
        Assert.Equal(0, repo.Saves);
    }

    [Theory]
    [InlineData(24, 64, 256)]
    [InlineData(16, 32, 256)]
    [InlineData(16, 64, 512)]
    public async Task CustomLimitsRemainUnchangedUntilProfileIsSelectedAndSaved(int sessions, int query, int total)
    {
        var limits = new McpContinuationLimits
            { MaximumSessions = sessions, MemoryPerQueryMiB = query, TotalMemoryMiB = total };
        var repo = new Repository { Settings = new AppSettings { McpContinuationLimits = limits } };
        var vm = new McpLimitsViewModel(repo);
        await vm.LoadAsync();
        Assert.True(vm.CanEdit);
        Assert.Null(vm.SelectedProfile);
        Assert.Equal(sessions, vm.MaximumSessions);
        Assert.Equal(query, vm.MemoryPerQueryMiB);
        Assert.Equal(total, vm.TotalMemoryMiB);
        Assert.Contains("do not match", vm.ProfileNotice);
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, repo.Saves);
        vm.RestoreDefaultsCommand.Execute(null);
        Assert.Same(McpLimitsViewModel.Profiles[1], vm.SelectedProfile);
        Assert.Null(vm.ProfileNotice);
        Assert.Equal(limits, repo.Settings.McpContinuationLimits);
        vm.SelectedProfile = McpLimitsViewModel.Profiles[0];
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(McpLimitsViewModel.Profiles[0].Limits, repo.Settings.McpContinuationLimits);
    }

    [Fact]
    public async Task NoSelectionCannotSave()
    {
        var repo = new Repository();
        var vm = new McpLimitsViewModel(repo);
        await vm.LoadAsync();
        vm.SelectedProfile = null;
        Assert.False(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, repo.Saves);
    }

    [Fact]
    public async Task SettingsSaveAndImportExportPreserveMcpLimits()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var scope = AppPaths.BeginTestScope(rootPath: root);
        try
        {
            var repo = new JsonSettingsRepository();
            var limits = new McpContinuationLimits { MaximumSessions = 24, MemoryPerQueryMiB = 128, TotalMemoryMiB = 512 };
            await repo.SaveAsync(new AppSettings { McpContinuationLimits = limits });
            var vm = new SettingsViewModel(repo);
            await vm.LoadAsync();
            vm.LogFontSize = 14;
            await vm.SaveAsync();
            Assert.Equal(limits, (await repo.LoadAsync()).McpContinuationLimits);
            var export = Path.Combine(root, "export.json");
            await repo.SaveToFileAsync(export, await repo.LoadAsync());
            var imported = await repo.LoadFromFileAsync(export);
            await repo.SaveAsync(imported);
            await vm.LoadAsync();
            await vm.SaveAsync();
            Assert.Equal(limits, (await repo.LoadAsync()).McpContinuationLimits);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(AppTheme.Default)]
    [InlineData(AppTheme.EasyReading)]
    [InlineData(AppTheme.Dark)]
    public async Task DialogSelectsProfilesSavesAndRendersInEveryTheme(AppTheme theme)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var previousTheme = (AppTheme)Application.Current.Resources["AppThemeResource"];
            new WpfLogAppearanceService().Apply(new AppSettings { Theme = theme });
            var custom = new McpContinuationLimits { MaximumSessions = 24, MemoryPerQueryMiB = 128, TotalMemoryMiB = 512 };
            var repo = new Repository { Settings = new AppSettings { McpContinuationLimits = custom } };
            var presentation = McpHelpPresentationBuilder.Create(new McpHelpDialogRequest(2),
                McpServerLocationResolver.ResolvePackaged(AppContext.BaseDirectory), _ => true);
            var window = new McpHelpWindow(presentation, new Actions(), repo);
            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();
                var profiles = Assert.IsType<ComboBox>(window.FindName("CapacityProfileBox"));
                var sessions = Assert.IsType<TextBlock>(window.FindName("SessionsValue"));
                var query = Assert.IsType<TextBlock>(window.FindName("QueryMemoryValue"));
                var total = Assert.IsType<TextBlock>(window.FindName("TotalMemoryValue"));
                var save = Assert.IsType<Button>(window.FindName("SaveLimitsButton"));
                Assert.Equal(3, profiles.Items.Count);
                Assert.False(profiles.IsEditable);
                Assert.Null(profiles.SelectedItem);
                Assert.Equal("24", sessions.Text);
                Assert.Equal("128", query.Text);
                Assert.Equal("512", total.Text);
                Assert.False(save.IsEnabled);
                Assert.Contains("do not match", Assert.IsType<TextBlock>(window.FindName("ProfileNotice")).Text);
                Assert.Equal("Capacity profile", new ComboBoxAutomationPeer(profiles).GetName());
                Capture(window, theme, "custom");
                foreach (var profile in McpLimitsViewModel.Profiles)
                {
                    profiles.SelectedItem = profile;
                    await WpfTestHost.FlushAsync();
                    Assert.Same(profile, window.Limits.SelectedProfile);
                    Assert.Equal(profile.Name, FindVisual<TextBlock>(profiles).Text);
                    Assert.Equal(profile.Limits.MaximumSessions.ToString(), sessions.Text);
                    Assert.Equal(profile.Limits.MemoryPerQueryMiB.ToString(), query.Text);
                    Assert.Equal(profile.Limits.TotalMemoryMiB.ToString(), total.Text);
                    Assert.True(save.IsEnabled);
                    Capture(window, theme, profile.Name.Replace(' ', '-'));
                }
                profiles.SelectedIndex = 0;
                profiles.Focus();
                profiles.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(profiles), Environment.TickCount, Key.Down)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                await WpfTestHost.FlushAsync();
                Assert.Equal(1, profiles.SelectedIndex);
                Assert.True(profiles.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(save, Keyboard.FocusedElement);
                profiles.IsDropDownOpen = true;
                await WpfTestHost.FlushAsync();
                Assert.True(profiles.IsDropDownOpen);
                Capture(window, theme, "dropdown");
                profiles.IsDropDownOpen = false;
                profiles.SelectedIndex = 2;
                repo.SaveCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var peer = new ButtonAutomationPeer(save);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await WpfTestHost.FlushAsync();
                Assert.False(profiles.IsEnabled);
                Assert.False(save.IsEnabled);
                window.Close();
                Assert.True(window.IsVisible);
                repo.SaveCompletion.SetResult();
                await WpfTestHost.FlushAsync();
                Assert.True(profiles.IsEnabled);
                Assert.Equal(McpLimitsViewModel.Profiles[2].Limits, repo.Settings.McpContinuationLimits);
                Assert.Contains("Restart", Assert.IsType<TextBlock>(window.FindName("LimitsStatus")).Text);
                Capture(window, theme, "saved");
                window.Limits.RestoreDefaultsCommand.Execute(null);
                Assert.Equal("16", sessions.Text);
                Assert.Equal(McpLimitsViewModel.Profiles[2].Limits, repo.Settings.McpContinuationLimits);
                var scroll = FindVisual<ScrollViewer>(window);
                scroll.ScrollToBottom();
                await WpfTestHost.FlushAsync();
                Assert.True(scroll.VerticalOffset > 0);
                window.Close();
                Assert.Equal(1, repo.Saves);
            }
            finally
            {
                window.Close();
                new WpfLogAppearanceService().Apply(new AppSettings { Theme = previousTheme });
            }
        });
    }

    private static T FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            try { return FindVisual<T>(VisualTreeHelper.GetChild(parent, i)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Visual not found.");
    }

    private static void Capture(Window window, AppTheme theme, string state)
    {
        var output = Environment.GetEnvironmentVariable("WEEZTAIL_MCP_LIMITS_SMOKE_DIR");
        foreach (var minimum in new[] { false, true })
        {
            window.Width = minimum ? window.MinWidth : 780;
            window.Height = minimum ? window.MinHeight : 700;
            window.UpdateLayout();
            var box = Assert.IsType<ComboBox>(window.FindName("CapacityProfileBox"));
            Assert.True(box.ActualWidth > 0);
            var point = box.TranslatePoint(new Point(box.ActualWidth, 0), window);
            Assert.True(point.X <= window.ActualWidth);
            if (string.IsNullOrEmpty(output)) continue;
            Directory.CreateDirectory(output);
            Render(window, window.ActualWidth, window.ActualHeight,
                Path.Combine(output, $"mcp-limits-{theme}-{state}-{(minimum ? "minimum" : "normal")}.png"));
            if (box.IsDropDownOpen)
            {
                var popup = Assert.IsType<Popup>(box.Template.FindName("PART_Popup", box));
                var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                Assert.True(child.ActualWidth > 0 && child.ActualHeight > 0);
                Render(child, child.ActualWidth, child.ActualHeight,
                    Path.Combine(output, $"mcp-limits-{theme}-popup-{(minimum ? "minimum" : "normal")}.png"));
            }
        }
    }

    private static void Render(Visual visual, double width, double height, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class Actions : IMcpHelpActions
    {
        public void CopyServerPath(Window owner, string serverExecutablePath) { }
        public void OpenGuide(Window owner, Uri guideUri) { }
    }

    private sealed class Repository : ISettingsRepository
    {
        public AppSettings Settings { get; set; } = new();
        public bool FailSave { get; set; }
        public bool FailLoad { get; set; }
        public TaskCompletionSource? SaveCompletion { get; set; }
        public int Saves { get; private set; }
        public Task<AppSettings> LoadAsync() => FailLoad
            ? Task.FromException<AppSettings>(new IOException("Test load failure."))
            : Task.FromResult(new AppSettings
        {
            LogFontSize = Settings.LogFontSize,
            McpContinuationLimits = Settings.McpContinuationLimits
        });
        public async Task SaveAsync(AppSettings settings)
        {
            if (FailSave) throw new IOException("Test save failure.");
            if (SaveCompletion != null) await SaveCompletion.Task;
            Settings = settings;
            Saves++;
        }
        public Task<AppSettings> LoadFromFileAsync(string path) => throw new NotSupportedException();
        public Task SaveToFileAsync(string path, AppSettings settings) => throw new NotSupportedException();
    }
}
