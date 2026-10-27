namespace LogReader.Tests;

using System.Windows;
using LogReader.App.Services;

public sealed class SeasonalShortcutIconTests
{
    [Fact]
    public void Controller_SwitchesShortcutAndRestoresWithoutRewritingUnchangedFiles()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var app = Application.Current;
            var original = app.Resources[SeasonalIcons.ResourceKey];
            var date = new DateOnly(2026, 10, 25);
            var shortcuts = fixture.CreateCoordinator();
            using var controller = new SeasonalWindowIconController(app, () => date, refreshShortcut: shortcuts.Refresh);
            try
            {
                controller.Start();
                Assert.True(shortcuts.IsRegistered);
                var standard = ShellShortcut.Read(fixture.ShortcutPath);
                Assert.Equal(fixture.Info.LauncherPath, standard.IconPath);
                Assert.False(Directory.Exists(fixture.Cache));

                date = new DateOnly(2026, 10, 26);
                controller.Refresh();
                var seasonal = ShellShortcut.Read(fixture.ShortcutPath);
                Assert.Equal(standard with { IconPath = seasonal.IconPath }, seasonal);
                Assert.StartsWith(fixture.Cache, seasonal.IconPath);
                AssertBundledIcon(seasonal.IconPath);
                var linkBytes = File.ReadAllBytes(fixture.ShortcutPath);
                var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(fixture.ShortcutPath, timestamp);
                File.SetLastWriteTimeUtc(seasonal.IconPath, timestamp);
                controller.Refresh();
                Assert.Equal(linkBytes, File.ReadAllBytes(fixture.ShortcutPath));
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.ShortcutPath));
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(seasonal.IconPath));

                date = new DateOnly(2026, 11, 2);
                controller.Refresh();
                Assert.Equal(standard, ShellShortcut.Read(fixture.ShortcutPath));
                Assert.True(File.Exists(seasonal.IconPath)); // Survives shutdown and release cleanup.
                controller.Dispose();
                date = new DateOnly(2027, 10, 31);
                controller.Refresh();
                Assert.Equal(standard, ShellShortcut.Read(fixture.ShortcutPath));
            }
            finally
            {
                controller.Dispose();
                app.Resources[SeasonalIcons.ResourceKey] = original;
            }
        });

    [Fact]
    public void RestartAndPromotion_ReuseSeasonalAssetAndRestoreAfterHoliday()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var legacy = Path.Combine(fixture.Programs, fixture.Info.LegacyShortcutRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            ShellShortcut.Save(legacy, fixture.Info);
            var standard = ShellShortcut.Read(legacy);
            var first = fixture.CreateCoordinator();
            first.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(first.IsRegistered);
            Assert.False(File.Exists(legacy));
            var seasonal = ShellShortcut.Read(fixture.ShortcutPath);
            Assert.Equal(standard with { IconPath = seasonal.IconPath }, seasonal);
            AssertBundledIcon(seasonal.IconPath);
            var promoted = TaskbarRelaunchInfo.TryCreate(fixture.Info.LauncherPath,
                Path.Combine(fixture.Root, "releases", "2.0.0"))!;
            var restarted = fixture.CreateCoordinator(promoted);
            restarted.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.Equal(seasonal, ShellShortcut.Read(fixture.ShortcutPath));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(seasonal.IconPath)!));
            restarted.Refresh(SeasonalIcons.StandardIcon);
            Assert.True(restarted.IsRegistered);
            Assert.Equal(standard, ShellShortcut.Read(fixture.ShortcutPath));
            Assert.Equal(fixture.Info.LauncherPath, ShellShortcut.Read(fixture.ShortcutPath).IconPath);
            AssertBundledIcon(seasonal.IconPath);
        });

    [Fact]
    public void Controller_AllHolidaysAndYearEndHandoffUseMatchingShortcutBytesAndCatchUpOnRestart()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var app = Application.Current;
            var original = app.Resources[SeasonalIcons.ResourceKey];
            var date = new DateOnly(2026, 10, 25);
            var shortcuts = fixture.CreateCoordinator();
            using var controller = new SeasonalWindowIconController(app, () => date, refreshShortcut: shortcuts.Refresh);
            SeasonalWindowIconController? restarted = null;
            try
            {
                controller.Start();
                Assert.True(shortcuts.IsRegistered);
                var standard = ShellShortcut.Read(fixture.ShortcutPath);
                object? previousImage = original;
                string? previousResource = null;
                foreach (var (nextDate, resource) in new[]
                {
                    (new DateOnly(2026, 10, 26), SeasonalIcons.HalloweenIcon),
                    (new DateOnly(2026, 11, 21), SeasonalIcons.ThanksgivingIcon),
                    (new DateOnly(2026, 12, 20), SeasonalIcons.ChristmasIcon),
                    (new DateOnly(2026, 12, 26), SeasonalIcons.ChristmasIcon),
                    (new DateOnly(2026, 12, 27), SeasonalIcons.NewYearIcon),
                    (new DateOnly(2027, 1, 2), SeasonalIcons.NewYearIcon)
                })
                {
                    date = nextDate;
                    controller.Refresh();
                    var link = ShellShortcut.Read(fixture.ShortcutPath);
                    Assert.True(shortcuts.IsRegistered);
                    Assert.Equal(standard with { IconPath = link.IconPath }, link);
                    Assert.Equal(ReadBundledIcon(resource), File.ReadAllBytes(link.IconPath));
                    var image = app.Resources[SeasonalIcons.ResourceKey];
                    Assert.NotSame(original, image);
                    // Repeated dates in one holiday retain the cached decoded image.
                    if (resource == previousResource)
                        Assert.Same(previousImage, image);
                    else
                        Assert.NotSame(previousImage, image);
                    previousImage = image;
                    previousResource = resource;
                }
                controller.Dispose();
                date = new DateOnly(2027, 1, 3); // Closed across the holiday boundary.
                app.Resources[SeasonalIcons.ResourceKey] = original; // Fresh application resources.
                var restartedShortcuts = fixture.CreateCoordinator();
                restarted = new SeasonalWindowIconController(app, () => date,
                    refreshShortcut: restartedShortcuts.Refresh);
                restarted.Start();
                Assert.True(restartedShortcuts.IsRegistered);
                Assert.Equal(standard, ShellShortcut.Read(fixture.ShortcutPath));
                Assert.Same(original, app.Resources[SeasonalIcons.ResourceKey]);
            }
            finally
            {
                restarted?.Dispose();
                controller.Dispose();
                app.Resources[SeasonalIcons.ResourceKey] = original;
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacySeasonalIcon_MigratesOnRestartDuringOrAfterHoliday(bool afterHoliday)
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var first = fixture.CreateCoordinator();
            first.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(first.IsRegistered);
            var seasonal = ShellShortcut.Read(fixture.ShortcutPath);
            var legacy = Path.Combine(fixture.Programs, fixture.Info.LegacyShortcutRelativePath);
            ShellShortcut.Save(legacy, fixture.Info, seasonal.IconPath);
            File.Delete(fixture.ShortcutPath);
            var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(seasonal.IconPath, timestamp);

            var restarted = fixture.CreateCoordinator();
            restarted.Refresh(afterHoliday ? SeasonalIcons.StandardIcon : SeasonalIcons.HalloweenIcon);
            Assert.True(restarted.IsRegistered);
            Assert.False(File.Exists(legacy));
            var friendly = ShellShortcut.Read(fixture.ShortcutPath);
            Assert.Equal(seasonal with { IconPath = afterHoliday ? fixture.Info.LauncherPath : seasonal.IconPath }, friendly);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(seasonal.IconPath));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyIcon_UnknownCachePathOrOtherDeploymentIsPreserved(bool otherDeployment)
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var other = TaskbarRelaunchInfo.TryCreate(Path.Combine(fixture.Root, "Other", "WeezTail.Launcher.exe"),
                Path.Combine(fixture.Root, "Other", "releases", "1.2.3"))!;
            var iconOwner = fixture.CreateCoordinator(otherDeployment ? other : fixture.Info);
            iconOwner.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(iconOwner.IsRegistered);
            var source = ShellShortcut.Read(Path.Combine(fixture.Programs,
                (otherDeployment ? other : fixture.Info).ShortcutRelativePath)).IconPath;
            var icon = source;
            if (!otherDeployment)
            {
                // Even bundled bytes in the correct cache directory do not prove path ownership.
                icon = Path.Combine(Path.GetDirectoryName(source)!, new string('a', 43) + ".ico");
                File.Copy(source, icon);
            }
            var legacy = Path.Combine(fixture.Programs, fixture.Info.LegacyShortcutRelativePath);
            ShellShortcut.Save(legacy, fixture.Info, icon);
            var before = File.ReadAllBytes(legacy);
            var restarted = fixture.CreateCoordinator();
            foreach (var resource in new[] { SeasonalIcons.StandardIcon, SeasonalIcons.HalloweenIcon, SeasonalIcons.StandardIcon })
            {
                restarted.Refresh(resource);
                Assert.False(restarted.IsRegistered);
                Assert.Equal(before, File.ReadAllBytes(legacy));
                Assert.Equal(fixture.Info.LauncherPath, ShellShortcut.Read(fixture.ShortcutPath).Target);
            }
        });

    [Fact]
    public void LegacySeasonalIcon_ResourceReadFailurePreservesLinkAndRetriesCleanup()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var first = fixture.CreateCoordinator();
            first.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(first.IsRegistered);
            var seasonal = ShellShortcut.Read(fixture.ShortcutPath);
            var legacy = Path.Combine(fixture.Programs, fixture.Info.LegacyShortcutRelativePath);
            ShellShortcut.Save(legacy, fixture.Info, seasonal.IconPath);
            File.Delete(fixture.ShortcutPath);
            var before = File.ReadAllBytes(legacy);
            var restarted = fixture.CreateCoordinator(readIcon: _ => throw new IOException("Test ownership read failure"));
            restarted.Refresh(SeasonalIcons.StandardIcon);
            Assert.False(restarted.IsRegistered);
            Assert.Equal(before, File.ReadAllBytes(legacy));
            Assert.Equal(fixture.Info.LauncherPath, ShellShortcut.Read(fixture.ShortcutPath).IconPath);

            first.Refresh(SeasonalIcons.StandardIcon);
            Assert.True(first.IsRegistered);
            Assert.False(File.Exists(legacy));
        });

    [Fact]
    public void Refresh_RepairsMissingShortcutAndMissingOrCorruptedCachedIcon()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var shortcuts = fixture.CreateCoordinator();
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(shortcuts.IsRegistered);
            var seasonal = ShellShortcut.Read(fixture.ShortcutPath);
            File.Delete(fixture.ShortcutPath);
            File.Delete(seasonal.IconPath);
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.Equal(seasonal, ShellShortcut.Read(fixture.ShortcutPath));
            AssertBundledIcon(seasonal.IconPath);
            File.WriteAllText(seasonal.IconPath, "Corrupt cache entry");
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            AssertBundledIcon(seasonal.IconPath);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(seasonal.IconPath)!));
        });

    [Fact]
    public void AssetReadFailure_UsesStandardIconAndRetriesOnNextRefresh()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var reads = 0;
            var shortcuts = fixture.CreateCoordinator(readIcon: resource =>
            {
                if (++reads == 1)
                    throw new IOException("Test resource read failure");
                return ReadBundledIcon(resource);
            });
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(shortcuts.IsRegistered);
            Assert.Equal(fixture.Info.LauncherPath, ShellShortcut.Read(fixture.ShortcutPath).IconPath);
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            AssertBundledIcon(ShellShortcut.Read(fixture.ShortcutPath).IconPath);
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.Equal(2, reads);
        });

    [Fact]
    public void UnwritableCache_UsesStandardIconAndRecoversWithoutRestart()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            File.WriteAllText(fixture.Cache, "File blocks cache directory creation");
            var shortcuts = fixture.CreateCoordinator();
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(shortcuts.IsRegistered);
            Assert.Equal(fixture.Info.LauncherPath, ShellShortcut.Read(fixture.ShortcutPath).IconPath);
            File.Delete(fixture.Cache);
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            AssertBundledIcon(ShellShortcut.Read(fixture.ShortcutPath).IconPath);
        });

    [Fact]
    public void ExcessiveCachePath_FallsBackToLauncherWithoutCreatingAnUnusableIcon()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var cache = Path.Combine(fixture.Cache, new string('x', 150));
            var shortcuts = new SeasonalShortcutIconCoordinator(fixture.Info, fixture.Programs, cache);
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(shortcuts.IsRegistered);
            Assert.Equal(fixture.Info.LauncherPath, ShellShortcut.Read(fixture.ShortcutPath).IconPath);
            Assert.False(Directory.Exists(fixture.Cache));
        });

    [Fact]
    public void LockedShortcut_PreservesPreviousLinkAndRetriesAfterUnlock()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var shortcuts = fixture.CreateCoordinator();
            shortcuts.Refresh(SeasonalIcons.StandardIcon);
            Assert.True(shortcuts.IsRegistered);
            var before = File.ReadAllBytes(fixture.ShortcutPath);
            using (File.Open(fixture.ShortcutPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
                Assert.False(shortcuts.IsRegistered);
                Assert.Equal(before, File.ReadAllBytes(fixture.ShortcutPath));
            }
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.ShortcutPath)!));
            shortcuts.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(shortcuts.IsRegistered);
            AssertBundledIcon(ShellShortcut.Read(fixture.ShortcutPath).IconPath);
        });

    [Fact]
    public void Deployments_KeepTheirShortcutsAndCachedAssetsIndependent()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            var other = TaskbarRelaunchInfo.TryCreate(Path.Combine(fixture.Root, "Other", "WeezTail.Launcher.exe"),
                Path.Combine(fixture.Root, "Other", "releases", "1.2.3"))!;
            var first = fixture.CreateCoordinator();
            var second = fixture.CreateCoordinator(other);
            first.Refresh(SeasonalIcons.HalloweenIcon);
            second.Refresh(SeasonalIcons.HalloweenIcon);
            Assert.True(first.IsRegistered);
            Assert.True(second.IsRegistered);
            var otherPath = Path.Combine(fixture.Programs, other.ShortcutRelativePath);
            var otherBytes = File.ReadAllBytes(otherPath);
            var firstIcon = ShellShortcut.Read(fixture.ShortcutPath).IconPath;
            var secondIcon = ShellShortcut.Read(otherPath).IconPath;
            Assert.NotEqual(firstIcon, secondIcon);
            first.Refresh(SeasonalIcons.StandardIcon);
            Assert.Equal(otherBytes, File.ReadAllBytes(otherPath));
            AssertBundledIcon(secondIcon);
        });

    [Fact]
    public async Task ConcurrentInstances_SkipContendedUpdateAndRetryAfterOwnerFinishes()
    {
        using var fixture = new Deployment();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var registrations = 0;
        var owner = fixture.CreateCoordinator(registerShortcut: _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Owner registration was not released.");
            return true;
        });
        var contender = fixture.CreateCoordinator(registerShortcut: _ =>
        {
            Interlocked.Increment(ref registrations);
            return true;
        });
        var ownedWork = Task.Run(() => owner.Refresh(SeasonalIcons.StandardIcon));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "Owner did not acquire update lock.");
            contender.Refresh(SeasonalIcons.StandardIcon);
            Assert.False(contender.IsRegistered);
            Assert.Equal(0, Volatile.Read(ref registrations));
        }
        finally
        {
            release.Set();
            await ownedWork.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(owner.IsRegistered);
        contender.Refresh(SeasonalIcons.StandardIcon);
        Assert.True(contender.IsRegistered);
        Assert.Equal(1, registrations);
    }

    private static void AssertBundledIcon(string path)
        => Assert.Equal(ReadBundledIcon(SeasonalIcons.HalloweenIcon), File.ReadAllBytes(path));

    private static byte[] ReadBundledIcon(string resource)
    {
        using var stream = Application.GetResourceStream(new Uri(resource))!.Stream;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed class Deployment : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "WeezTail seasonal shortcut é", Guid.NewGuid().ToString("N"));
        internal string Programs => Path.Combine(Root, "Programs");
        internal string Cache => Path.Combine(Root, "LocalAppData");
        internal TaskbarRelaunchInfo Info => TaskbarRelaunchInfo.TryCreate(Path.Combine(Root, "WeezTail.Launcher.exe"),
            Path.Combine(Root, "releases", "1.2.3"))!;
        internal string ShortcutPath => Path.Combine(Programs, Info.ShortcutRelativePath);

        internal Deployment() => Directory.CreateDirectory(Root);
        internal SeasonalShortcutIconCoordinator CreateCoordinator(TaskbarRelaunchInfo? info = null,
            Func<string, byte[]>? readIcon = null, Func<string, bool>? registerShortcut = null)
            => new(info ?? Info, Programs, Cache, readIcon, registerShortcut);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
