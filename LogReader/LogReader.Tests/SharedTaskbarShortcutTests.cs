namespace LogReader.Tests;

using LogReader.App.Services;

public sealed class SharedTaskbarShortcutTests
{
    [Fact]
    public void Registration_RoundTripsAndRefreshesOnlyTheDeploymentShortcut()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var info = fixture.Info;
            var other = TaskbarRelaunchInfo.TryCreate(
                Path.Combine(fixture.Root, "Other", "WeezTail.Launcher.exe"),
                Path.Combine(fixture.Root, "Other", "releases", "1.2.3"))!;
            Assert.True(SharedTaskbarShortcut.TryRegister(other, fixture.Programs));
            var otherPath = fixture.ShortcutPath(other);
            var otherBytes = File.ReadAllBytes(otherPath);
            var unrelatedPath = Path.Combine(Path.GetDirectoryName(otherPath)!, "My shortcut.lnk");
            File.WriteAllText(unrelatedPath, "User-owned shortcut");

            Assert.True(SharedTaskbarShortcut.TryRegister(info, fixture.Programs));
            var shortcut = ShellShortcut.Read(fixture.ShortcutPath(info));
            Assert.Equal(info.LauncherPath, shortcut.Target);
            Assert.Equal(info.DeploymentRoot, shortcut.WorkingDirectory);
            Assert.Equal(string.Empty, shortcut.Arguments);
            Assert.Equal(info.LauncherPath, shortcut.IconPath);
            Assert.Equal(0, shortcut.IconIndex);
            Assert.Equal(info.AppUserModelId, shortcut.AppUserModelId);

            // Promotion refreshes the same shortcut without creating another identity.
            var next = TaskbarRelaunchInfo.TryCreate(info.LauncherPath,
                Path.Combine(fixture.Root, "releases", "2.0.0"))!;
            Assert.Equal(info.ShortcutFileName, next.ShortcutFileName);
            Assert.True(SharedTaskbarShortcut.TryRegister(next, fixture.Programs));
            Assert.Equal(shortcut, ShellShortcut.Read(fixture.ShortcutPath(next)));
            Assert.Equal(otherBytes, File.ReadAllBytes(otherPath));
            Assert.Equal("User-owned shortcut", File.ReadAllText(unrelatedPath));
            Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(otherPath)!).Length);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_DoesNotOverwriteUnrelatedDestination(bool sameIdentity)
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var path = fixture.ShortcutPath(fixture.Info);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var foreign = new TaskbarRelaunchInfo(Path.Combine(fixture.Root, "Other.exe"),
                sameIdentity ? fixture.Info.AppUserModelId : "Other.Application");
            ShellShortcut.Save(path, foreign);
            var before = File.ReadAllBytes(path);
            Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        });

    [Fact]
    public void Registration_InvalidExistingShortcutIsNonfatalAndPreserved()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var path = fixture.ShortcutPath(fixture.Info);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Not a Shell link");
            Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            Assert.Equal("Not a Shell link", File.ReadAllText(path));
        });

    [Fact]
    public void Registration_LockedOwnedShortcutKeepsPreviousLinkAndCleansTemporaryFile()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            Assert.True(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            var path = fixture.ShortcutPath(fixture.Info);
            var before = File.ReadAllBytes(path);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs,
                    Path.Combine(fixture.Root, "seasonal.ico")));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        });

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void Registration_UnavailableProgramsDirectoryIsNonfatal(string directory)
    {
        var info = TaskbarRelaunchInfo.TryCreate(@"C:\Shared\WeezTail.Launcher.exe", @"C:\Shared\releases\1.2.3")!;
        Assert.False(SharedTaskbarShortcut.TryRegister(info, directory));
    }

    [Fact]
    public void Registration_UnwritableProgramsLocationIsNonfatal()
    {
        using var fixture = new Deployment();
        File.WriteAllText(fixture.Programs, "A file blocks directory creation");
        Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
        Assert.Equal("A file blocks directory creation", File.ReadAllText(fixture.Programs));
    }

    [Fact]
    public void Discovery_ReadsRealDeploymentWithPromotedPointerAndMissingConfig()
    {
        using var fixture = new Deployment();
        var release = Path.Combine(fixture.Root, "releases", "1.2.3");
        Directory.CreateDirectory(release);
        File.WriteAllText(fixture.Info.LauncherPath, string.Empty);
        File.WriteAllText(Path.Combine(fixture.Root, "current.json"), "{\"version\":\"2.0.0\"}");
        var config = Path.Combine(release, "WeezTail.install.json");
        File.WriteAllText(config, "{\"installMode\":\"Portable\",\"storageMode\":\"LocalAppData\"}");
        Assert.Equal(fixture.Info, TaskbarRelaunchInfo.Discover(release));
        File.Delete(config);
        Assert.Null(TaskbarRelaunchInfo.Discover(release));
    }

    private sealed class Deployment : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "WeezTail taskbar é", Guid.NewGuid().ToString("N"));
        internal string Programs => Path.Combine(Root, "Programs");
        internal TaskbarRelaunchInfo Info => TaskbarRelaunchInfo.TryCreate(Path.Combine(Root, "WeezTail.Launcher.exe"),
            Path.Combine(Root, "releases", "1.2.3"))!;

        internal Deployment() => Directory.CreateDirectory(Root);
        internal string ShortcutPath(TaskbarRelaunchInfo info) => Path.Combine(Programs, "WeezTail Shared", info.ShortcutFileName);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
