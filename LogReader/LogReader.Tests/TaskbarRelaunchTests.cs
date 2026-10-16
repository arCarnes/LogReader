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
    public void Metadata_ClearsHandoffBeforeReturning(string? value)
    {
        var cleared = false;
        var read = false;
        var info = TaskbarRelaunchInfo.ConsumeEnvironment(
            () => { read = true; return value; },
            () => { Assert.True(read); cleared = true; },
            @"C:\Shared\releases\1.2.3");
        Assert.True(cleared);
        Assert.Equal(value == @"C:\Shared\WeezTail.Launcher.exe", info != null);
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
                    using var properties = new WindowPropertyStore(new WindowInteropHelper(window).Handle);
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
}
