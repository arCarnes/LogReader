namespace LogReader.Tests;

using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LogReader.App.Services;
using LogReader.App.Views;
using LogReaderApplication = LogReader.App.App;

public sealed class SeasonalWindowIconTests
{
    [Fact]
    public void BundledIcon_HasEveryApprovedSizeAndRealTransparency()
        => WpfTestHost.Run(() =>
        {
            using var stream = Application.GetResourceStream(new Uri(SeasonalIcons.HalloweenIcon))!.Stream;
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 },
                decoder.Frames.Select(frame => frame.PixelWidth).Order().ToArray());
            foreach (var frame in decoder.Frames)
            {
                Assert.Equal(frame.PixelWidth, frame.PixelHeight);
                var pixels = Pixels(frame);
                Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha == 0);
                Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha == 255);
            }
        });

    [Fact]
    public void Controller_RefreshesBothDirectionsAndCachesAssetsAcrossDateJumps()
        => WpfTestHost.Run(() =>
        {
            var app = Application.Current;
            var original = app.Resources[SeasonalIcons.ResourceKey];
            var date = new DateOnly(2026, 10, 25);
            var reads = 0;
            var loads = 0;
            var image = new DrawingImage();
            using var controller = new SeasonalWindowIconController(app, () => { reads++; return date; },
                loadIcon: _ => { loads++; return image; });
            try
            {
                controller.Start();
                Assert.True(controller.IsRunning);
                Assert.Same(original, app.Resources[SeasonalIcons.ResourceKey]);
                Assert.Equal(0, loads);
                date = new DateOnly(2026, 10, 26);
                controller.Refresh();
                Assert.Same(image, app.Resources[SeasonalIcons.ResourceKey]);
                controller.Start(); // Starting twice must not duplicate work/subscriptions.
                var readsBeforeActivation = reads;
                ActivateApplication(app);
                Assert.Equal(readsBeforeActivation + 1, reads);
                Assert.Equal(1, loads);
                date = new DateOnly(2026, 11, 2);
                controller.Refresh();
                Assert.Same(original, app.Resources[SeasonalIcons.ResourceKey]);
                date = new DateOnly(2027, 10, 31);
                controller.Refresh();
                Assert.Same(image, app.Resources[SeasonalIcons.ResourceKey]);
                Assert.Equal(1, loads);
                controller.Dispose();
                controller.Dispose();
                Assert.False(controller.IsRunning);
                var readsAtDisposal = reads;
                date = new DateOnly(2027, 11, 1);
                controller.Refresh();
                ActivateApplication(app);
                Assert.Equal(readsAtDisposal, reads);
                Assert.Same(image, app.Resources[SeasonalIcons.ResourceKey]);
                Assert.Throws<ObjectDisposedException>(controller.Start);
            }
            finally
            {
                controller.Dispose();
                app.Resources[SeasonalIcons.ResourceKey] = original;
            }
        });

    [Fact]
    public void Controller_BrokenAssetRestoresStandardAndDoesNotRetryEveryRefresh()
        => WpfTestHost.Run(() =>
        {
            var app = Application.Current;
            var original = app.Resources[SeasonalIcons.ResourceKey];
            var date = new DateOnly(2026, 10, 31);
            var loads = 0;
            var image = new DrawingImage();
            var broken = new SeasonalIconDefinition("broken", "broken.ico", new AnnualDateRangeRule(11, 2, 11, 3));
            using var controller = new SeasonalWindowIconController(app, () => date, [SeasonalIcons.Catalog[0], broken],
                resource =>
                {
                    loads++;
                    return resource == "broken.ico" ? throw new IOException("Missing test asset") : image;
                });
            try
            {
                controller.Start();
                Assert.Same(image, app.Resources[SeasonalIcons.ResourceKey]);
                date = new DateOnly(2026, 11, 2);
                controller.Refresh();
                Assert.Same(original, app.Resources[SeasonalIcons.ResourceKey]);
                controller.Refresh();
                Assert.Equal(2, loads);
                date = new DateOnly(2026, 10, 31);
                controller.Refresh();
                Assert.Same(image, app.Resources[SeasonalIcons.ResourceKey]);
            }
            finally
            {
                controller.Dispose();
                app.Resources[SeasonalIcons.ResourceKey] = original;
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_InitializesBeforeUiAndDisposesAfterFailedOrCanceledStartup(bool throws)
        => await WpfTestHost.RunAsync(async () =>
        {
            var app = Application.Current;
            var original = app.Resources[SeasonalIcons.ResourceKey];
            using var controller = new SeasonalWindowIconController(app, () => new DateOnly(2026, 10, 31));
            try
            {
                async Task Run() => await LogReaderApplication.RunWithSeasonalIconsAsync(controller, () =>
                {
                    Assert.True(controller.IsRunning);
                    Assert.NotSame(original, app.Resources[SeasonalIcons.ResourceKey]);
                    return throws ? Task.FromException<bool>(new IOException("Test startup failure")) : Task.FromResult(false);
                });
                if (throws)
                    await Assert.ThrowsAsync<IOException>(Run);
                else
                    await Run();
                Assert.False(controller.IsRunning);
                Assert.Throws<ObjectDisposedException>(controller.Start);
            }
            finally
            {
                controller.Dispose();
                app.Resources[SeasonalIcons.ResourceKey] = original;
            }
        });

    [Fact]
    public async Task Windows_ExistingAndNewWindowsFollowResourceAndRestoreTogether()
        => await WpfTestHost.RunAsync(async () =>
        {
            var app = Application.Current;
            var original = app.Resources[SeasonalIcons.ResourceKey];
            var date = new DateOnly(2026, 10, 25);
            using var controller = new SeasonalWindowIconController(app, () => date);
            var main = new MainWindow();
            SettingsWindow? later = null;
            try
            {
                await LogReaderApplication.RunWithSeasonalIconsAsync(controller, () => Task.FromResult(true));
                Assert.True(controller.IsRunning);
                WpfTestHost.ShowHidden(main);
                await WpfTestHost.FlushAsync();
                Assert.Same(original, main.Icon);
                date = new DateOnly(2026, 10, 26);
                controller.Refresh();
                await WpfTestHost.FlushAsync();
                var seasonal = app.Resources[SeasonalIcons.ResourceKey];
                Assert.NotSame(original, seasonal);
                Assert.Same(seasonal, main.Icon);
                later = new SettingsWindow();
                WpfTestHost.ShowHidden(later);
                await WpfTestHost.FlushAsync();
                Assert.Same(seasonal, later.Icon);
                date = new DateOnly(2026, 11, 2);
                controller.Refresh();
                await WpfTestHost.FlushAsync();
                Assert.Same(original, main.Icon);
                Assert.Same(original, later.Icon);
            }
            finally
            {
                controller.Dispose();
                later?.Close();
                main.Close();
                app.Resources[SeasonalIcons.ResourceKey] = original;
            }
        });

    [Fact]
    public void AllApplicationWindows_UseTheSharedIconBeforeTheyAreShown()
        => WpfTestHost.Run(() =>
        {
            var app = Application.Current;
            var original = app.Resources[SeasonalIcons.ResourceKey];
            var date = new DateOnly(2026, 10, 31);
            using var controller = new SeasonalWindowIconController(app, () => date);
            var windows = new List<Window>();
            try
            {
                controller.Start();
                windows.Add(new MainWindow());
                windows.Add(new SettingsWindow());
                windows.Add(new ControlsWindow());
                windows.Add(new DashboardTargetPickerWindow());
                windows.Add(new FileDisplayNameWindow(@"C:\logs\app.log", null));
                windows.Add(new BulkOpenDashboardPathsWindow(new BulkOpenPathsDialogRequest(BulkOpenPathsScope.AdHoc)));
                windows.Add(new StorageSetupWindow(new StubMessageBoxService()));
                windows.Add(new McpHelpWindow(
                    new McpHelpPresentation("", false, "", "", "", new Uri("https://example.test/guide")),
                    new UnusedMcpHelpActions(), new StubSettingsRepository()));
                var seasonal = app.Resources[SeasonalIcons.ResourceKey];
                Assert.NotSame(original, seasonal);
                Assert.All(windows, window => Assert.Same(seasonal, window.Icon));
                date = new DateOnly(2026, 11, 2);
                controller.Refresh();
                Assert.All(windows, window => Assert.Same(original, window.Icon));
            }
            finally
            {
                controller.Dispose();
                foreach (var window in windows)
                    window.Close();
                app.Resources[SeasonalIcons.ResourceKey] = original;
            }
        });

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public async Task NativeWindowIcon_ChangesAndRestoresWithoutChangingTaskbarRelaunchProperties()
    {
        InteractiveDesktop.RequireAvailable();
        await WpfTestHost.RunAsync(async () =>
        {
            var app = Application.Current;
            var original = app.Resources[SeasonalIcons.ResourceKey];
            var date = new DateOnly(2026, 10, 25);
            using var controller = new SeasonalWindowIconController(app, () => date);
            var info = TaskbarRelaunchInfo.TryCreate(@"C:\Shared\WeezTail.Launcher.exe", @"C:\Shared\releases\1.2.3")!;
            var window = new AppWindowFactory(info, _ => false).Create(null!).Window!;
            try
            {
                controller.Start();
                // Exercise a real taskbar window without covering the user's desktop.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
                window.Show();
                await WpfTestHost.FlushAsync();
                Assert.True(window.IsVisible);
                Assert.True(window.ShowInTaskbar);
                var handle = new WindowInteropHelper(window).Handle;
                var normal = NativeIconPixels(handle);
                date = new DateOnly(2026, 10, 31);
                controller.Refresh();
                await WpfTestHost.FlushAsync();
                Assert.False(normal.SequenceEqual(NativeIconPixels(handle)));
                using var properties = new ShellPropertyStore(handle);
                Assert.Equal(info.Command, properties.GetString(2));
                Assert.Equal(info.Icon, properties.GetString(3));
                Assert.Equal("WeezTail", properties.GetString(4));
                Assert.Equal(info.AppUserModelId, properties.GetString(5));
                date = new DateOnly(2026, 11, 2);
                controller.Refresh();
                await WpfTestHost.FlushAsync();
                Assert.Equal(normal, NativeIconPixels(handle));
                Assert.Equal(info.AppUserModelId, properties.GetString(5));
            }
            finally
            {
                controller.Dispose();
                window.Close();
                app.Resources[SeasonalIcons.ResourceKey] = original;
            }
        });
    }

    private static byte[] NativeIconPixels(IntPtr window)
    {
        var icon = SendMessage(window, 0x007F, new IntPtr(1), IntPtr.Zero); // WM_GETICON / ICON_BIG; borrowed handle.
        Assert.NotEqual(IntPtr.Zero, icon);
        return Pixels(Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()));
    }

    private static void ActivateApplication(Application application)
        => typeof(Application).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(application, [EventArgs.Empty]);

    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class UnusedMcpHelpActions : IMcpHelpActions
    {
        public void CopyServerPath(Window owner, string serverExecutablePath) => throw new InvalidOperationException("Unexpected clipboard action.");
        public void OpenGuide(Window owner, Uri guideUri) => throw new InvalidOperationException("Unexpected browser action.");
    }
}
