namespace LogReader.Tests;

using System.Windows;
using System.Windows.Controls;
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
        vm.MaximumSessions = "32";
        repo.Settings.LogFontSize = 18;
        repo.FailSave = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("could not be saved", vm.Status);
        Assert.Equal("32", vm.MaximumSessions);
        Assert.True(vm.CanSave);
        repo.FailSave = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(32, repo.Settings.McpContinuationLimits!.MaximumSessions);
        Assert.Equal(18, repo.Settings.LogFontSize);
        Assert.Contains("Restart", vm.Status);
        vm.RestoreDefaultsCommand.Execute(null);
        Assert.Equal("16", vm.MaximumSessions);
        Assert.Equal(32, repo.Settings.McpContinuationLimits.MaximumSessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public async Task InvalidDraftCannotSave(string input)
    {
        var repo = new Repository();
        var vm = new McpLimitsViewModel(repo);
        await vm.LoadAsync();
        vm.MaximumSessions = input;
        Assert.NotNull(vm.ValidationError);
        Assert.False(vm.SaveCommand.CanExecute(null));
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
    public async Task DialogEditsValidatesSavesAndRendersInEveryTheme(AppTheme theme)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var previousTheme = (AppTheme)Application.Current.Resources["AppThemeResource"];
            new WpfLogAppearanceService().Apply(new AppSettings { Theme = theme });
            var repo = new Repository();
            var presentation = McpHelpPresentationBuilder.Create(new McpHelpDialogRequest(2),
                McpServerLocationResolver.ResolvePackaged(AppContext.BaseDirectory), _ => true);
            var window = new McpHelpWindow(presentation, new Actions(), repo);
            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();
                var sessions = Assert.IsType<TextBox>(window.FindName("SessionsBox"));
                var query = Assert.IsType<TextBox>(window.FindName("QueryMemoryBox"));
                var total = Assert.IsType<TextBox>(window.FindName("TotalMemoryBox"));
                var save = Assert.IsType<Button>(window.FindName("SaveLimitsButton"));
                Assert.Equal("16", sessions.Text);
                Assert.Equal("64", query.Text);
                Assert.Equal("256", total.Text);
                Assert.True(save.IsEnabled);
                sessions.Focus();
                Assert.True(sessions.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(query, Keyboard.FocusedElement);
                Capture(window, theme, "valid");
                total.Text = "1";
                await WpfTestHost.FlushAsync();
                Assert.False(save.IsEnabled);
                Assert.Contains("at least 122 MiB", Assert.IsType<TextBlock>(window.FindName("LimitsValidation")).Text);
                Capture(window, theme, "invalid");
                total.Text = "512";
                sessions.Text = "32";
                await WpfTestHost.FlushAsync();
                var peer = new ButtonAutomationPeer(save);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await WpfTestHost.FlushAsync();
                Assert.Equal(32, repo.Settings.McpContinuationLimits!.MaximumSessions);
                Assert.Contains("Restart", Assert.IsType<TextBlock>(window.FindName("LimitsStatus")).Text);
                Capture(window, theme, "saved");
                window.Limits.RestoreDefaultsCommand.Execute(null);
                Assert.Equal("16", sessions.Text);
                Assert.Equal(32, repo.Settings.McpContinuationLimits.MaximumSessions);
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
            var box = Assert.IsType<TextBox>(window.FindName("TotalMemoryBox"));
            Assert.True(box.ActualWidth > 0);
            var point = box.TranslatePoint(new Point(box.ActualWidth, 0), window);
            Assert.True(point.X <= window.ActualWidth);
            if (string.IsNullOrEmpty(output)) continue;
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"mcp-limits-{theme}-{state}-{(minimum ? "minimum" : "normal")}.png"));
            encoder.Save(stream);
        }
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
