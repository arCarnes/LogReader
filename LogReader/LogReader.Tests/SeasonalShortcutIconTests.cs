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
