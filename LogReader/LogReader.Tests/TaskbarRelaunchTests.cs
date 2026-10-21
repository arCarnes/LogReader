namespace LogReader.Tests;

using System.Windows;
using System.Windows.Interop;
using LogReader.App.Services;

public sealed class TaskbarRelaunchTests
{
    [Theory]
    [InlineData(@"C:\Shared Apps\WeezTail")]
    [InlineData(@"\\server\shared apps\WeezTail")]
    public void Metadata_UsesStableLauncherAndIdentityAcrossPromotions(string root)
    {
        var launcher = Path.Combine(root, "WeezTail.Launcher.exe");
        var first = TaskbarRelaunchInfo.TryCreate(launcher, Path.Combine(root, "releases", "1.2.3"));
        var next = TaskbarRelaunchInfo.TryCreate(launcher, Path.Combine(root, "releases", "2.0.0") + "\\");
        Assert.NotNull(first);
        Assert.NotNull(next);
        Assert.Equal($"\"{launcher}\"", first.Command);
        Assert.Equal(launcher + ",0", first.Icon);
        Assert.Equal(first.AppUserModelId, next.AppUserModelId);
        Assert.Equal(first.AppUserModelId, TaskbarRelaunchInfo.TryCreate(
            launcher.ToUpperInvariant(), Path.Combine(root.ToUpperInvariant(), "RELEASES", "1.2.3"))!.AppUserModelId);
        Assert.NotEqual(first.AppUserModelId, TaskbarRelaunchInfo.TryCreate(
            Path.Combine(root + "Other", "WeezTail.Launcher.exe"),
            Path.Combine(root + "Other", "releases", "1.2.3"))!.AppUserModelId);
    }

    [Theory]
    [InlineData(null, @"C:\Shared\releases\1.2.3")]
    [InlineData("", @"C:\Shared\releases\1.2.3")]
    [InlineData("WeezTail.Launcher.exe", @"C:\Shared\releases\1.2.3")]
    [InlineData("C:\\Shared\0\\WeezTail.Launcher.exe", @"C:\Shared\releases\1.2.3")]
    [InlineData(@"C:\Other\WeezTail.Launcher.exe", @"C:\Shared\releases\1.2.3")]
    [InlineData(@"C:\Shared\WeezTail.Mcp.Launcher.exe", @"C:\Shared\releases\1.2.3")]
    [InlineData(@"C:\Shared\WeezTail.Launcher.exe", @"C:\Shared\versions\1.2.3")]
    [InlineData(@"C:\Shared\WeezTail.Launcher.exe", @"C:\Shared\releases\latest")]
    [InlineData(@"C:\Shared\WeezTail.Launcher.exe", @"C:\Shared\releases\1.2.3.4")]
    [InlineData(@"C:\Shared\WeezTail.Launcher.exe", @"C:\Program Files\WeezTail")]
    [InlineData("C:\\Shared\\WeezTail.Launcher.exe\" --extra", @"C:\Shared\releases\1.2.3")]
    public void Metadata_RejectsMissingOrUnrelatedLauncher(string? launcher, string release)
        => Assert.Null(TaskbarRelaunchInfo.TryCreate(launcher, release));

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Unrelated\WeezTail.Launcher.exe")]
    [InlineData(@"C:\Shared\WeezTail.Launcher.exe")]
    [InlineData("relative.exe")]
    [InlineData("C:\\Shared\\WeezTail.Launcher.exe\" --extra")]
    public void Metadata_ClearsHandoffBeforeReturning(string? value)
    {
        var cleared = false;
        var read = false;
        var info = TaskbarRelaunchInfo.ConsumeEnvironment(
            () => { read = true; return value; },
            () => { Assert.True(read); cleared = true; },
            @"C:\Shared\releases\1.2.3", _ => true,
            _ => "{\"installMode\":\"Portable\",\"storageMode\":\"LocalAppData\"}");
        Assert.True(cleared);
        Assert.NotNull(info);
        Assert.Equal(@"C:\Shared\WeezTail.Launcher.exe", info.LauncherPath);
    }

    [Theory]
    [InlineData(@"C:\Shared Apps\WeezTail")]
    [InlineData(@"\\server\shared apps\WeezTail")]
    [InlineData(@"S:\Shared Apps\WeezTail")]
    public void Discovery_UsesLayoutAndConfigWithoutReadingCurrentVersion(string root)
    {
        var reads = new List<string>();
        TaskbarRelaunchInfo Discover(string version) => TaskbarRelaunchInfo.Discover(
            Path.Combine(root, "releases", version), _ => true, path =>
            {
                reads.Add(path);
                return "{\"installMode\":\"Portable\",\"storageMode\":\"LocalAppData\"}";
            })!;
        var previous = Discover("1.2.3");
        var promoted = Discover("2.0.0");
        Assert.NotNull(previous);
        Assert.NotNull(promoted);
        Assert.Equal(previous, promoted);
        Assert.All(reads, path => Assert.Equal("WeezTail.install.json", Path.GetFileName(path)));
        Assert.Equal(TaskbarRelaunchInfo.TryCreate(Path.Combine(root, "WeezTail.Launcher.exe"),
            Path.Combine(root, "releases", "1.2.3")), previous);
    }

    [Theory]
    [InlineData("current.json")]
    [InlineData("WeezTail.Launcher.exe")]
    public void Discovery_RequiresPointerAndLauncher(string missing)
        => Assert.Null(TaskbarRelaunchInfo.Discover(@"C:\Shared\releases\1.2.3",
            path => Path.GetFileName(path) != missing, _ => throw new InvalidOperationException("Must not read config.")));

    [Theory]
    [InlineData("invalid JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"installMode\":5,\"storageMode\":\"LocalAppData\"}")]
    [InlineData("{\"installMode\":\"Msi\",\"storageMode\":\"LocalAppData\"}")]
    [InlineData("{\"installMode\":\"Dev\",\"storageMode\":\"LocalAppData\"}")]
    [InlineData("{\"installMode\":\"Portable\",\"storageMode\":\"ExeDirectory\"}")]
    public void Discovery_RejectsInvalidOrNonSharedConfiguration(string config)
        => Assert.Null(TaskbarRelaunchInfo.Discover(@"C:\Shared\releases\1.2.3", _ => true, _ => config));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discovery_ConfigReadFailureIsNonfatal(bool accessDenied)
        => Assert.Null(TaskbarRelaunchInfo.Discover(@"C:\Shared\releases\1.2.3", _ => true,
            _ =>
            {
                if (accessDenied)
                    throw new UnauthorizedAccessException();
                throw new FileNotFoundException();
            }));

    [Theory]
    [InlineData(@"C:\Program Files\WeezTail")]
    [InlineData(@"C:\Shared\versions\1.2.3")]
    [InlineData(@"C:\Shared\releases\latest")]
    [InlineData(@"C:\Shared\releases\1.2.3.4")]
    public void Discovery_DoesNotProbeUnrelatedLayouts(string directory)
        => Assert.Null(TaskbarRelaunchInfo.Discover(directory,
            _ => throw new InvalidOperationException("Must not probe an unrelated layout.")));

    [Fact]
    public void Metadata_UnrelatedHandoffCannotEnableOrdinaryInstallation()
    {
        var cleared = false;
        Assert.Null(TaskbarRelaunchInfo.ConsumeEnvironment(() => @"C:\Shared\WeezTail.Launcher.exe",
            () => cleared = true, @"C:\Program Files\WeezTail", _ => true));
        Assert.True(cleared);
    }

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void WindowProperties_KeepLauncherCommandAndIdentityAcrossPromotions()
    {
        InteractiveDesktop.RequireAvailable();
        WpfTestHost.Run(() =>
        {
            const string launcher = @"C:\Shared Apps\WeezTail\WeezTail.Launcher.exe";
            string? firstIdentity = null;
            foreach (var version in new[] { "1.2.3", "2.0.0" })
            {
                var info = TaskbarRelaunchInfo.TryCreate(launcher, @"C:\Shared Apps\WeezTail\releases\" + version)!;
                var window = new Window();
                window.SourceInitialized += (_, _) => TaskbarRelaunchProperties.Apply(window, info);
                try
                {
                    WpfTestHost.ShowHidden(window);
                    using var properties = new ShellPropertyStore(new WindowInteropHelper(window).Handle);
                    Assert.Equal(info.Command, properties.GetString(2));
                    Assert.Equal("WeezTail", properties.GetString(4));
                    Assert.Equal(info.Icon, properties.GetString(3));
                    Assert.Equal(info.AppUserModelId, properties.GetString(5));
                    if (firstIdentity != null)
                        Assert.Equal(firstIdentity, properties.GetString(5));
                    firstIdentity = properties.GetString(5);
                    TaskbarRelaunchProperties.Clear(new WindowInteropHelper(window).Handle);
                    foreach (var id in new uint[] { 2, 3, 4, 5 })
                        Assert.Null(properties.GetString(id));
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "InteractiveDesktop")]
    public void Factory_UsesShortcutIdentityOrRelaunchFallbackBeforeShowingWindow(bool registered)
    {
        InteractiveDesktop.RequireAvailable();
        WpfTestHost.Run(() =>
        {
            var info = TaskbarRelaunchInfo.TryCreate(@"C:\Shared\WeezTail.Launcher.exe", @"C:\Shared\releases\1.2.3")!;
            var registrations = 0;
            var factory = new AppWindowFactory(info, actual =>
            {
                Assert.Equal(info, actual);
                registrations++;
                return registered;
            });
            var window = factory.Create(null!).Window!;
            try
            {
                Assert.Equal(1, registrations);
                Assert.False(window.IsVisible);
                WpfTestHost.ShowHidden(window);
                using var properties = new ShellPropertyStore(new WindowInteropHelper(window).Handle);
                Assert.Equal(info.AppUserModelId, properties.GetString(5));
                Assert.Equal(registered ? null : info.Command, properties.GetString(2));
                Assert.Equal(registered ? null : "WeezTail", properties.GetString(4));
                Assert.Equal(registered ? null : info.Icon, properties.GetString(3));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Factory_OrdinaryStartupDoesNotRegisterShortcut()
        => WpfTestHost.Run(() =>
        {
            var factory = new AppWindowFactory(null, _ => throw new InvalidOperationException("Must not register."));
            factory.Create(null!).Window!.Close();
        });
}
