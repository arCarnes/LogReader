namespace LogReader.Tests;

using LogReader.App.Services;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

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
            var otherLegacy = Path.Combine(fixture.Programs, other.LegacyShortcutRelativePath);
            ShellShortcut.Save(otherLegacy, other);
            var otherLegacyBytes = File.ReadAllBytes(otherLegacy);
            var unrelatedPath = Path.Combine(Path.GetDirectoryName(otherPath)!, "My shortcut.lnk");
            File.WriteAllText(unrelatedPath, "User-owned shortcut");

            Assert.True(SharedTaskbarShortcut.TryRegister(info, fixture.Programs));
            Assert.Equal("WeezTail.lnk", Path.GetFileName(fixture.ShortcutPath(info)));
            Assert.NotEqual(fixture.ShortcutPath(info), otherPath);
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
            Assert.Equal(info.ShortcutRelativePath, next.ShortcutRelativePath);
            Assert.True(SharedTaskbarShortcut.TryRegister(next, fixture.Programs));
            Assert.Equal(shortcut, ShellShortcut.Read(fixture.ShortcutPath(next)));
            Assert.Equal(otherBytes, File.ReadAllBytes(otherPath));
            Assert.Equal(otherLegacyBytes, File.ReadAllBytes(otherLegacy));
            Assert.Equal("User-owned shortcut", File.ReadAllText(unrelatedPath));
            Assert.Equal(4, Directory.GetFiles(fixture.Programs, "*.lnk", SearchOption.AllDirectories).Length);
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

    [Fact]
    public void Registration_MigratesOwnedLegacyAndRetriesWithoutRewritingFriendlyLink()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            fixture.CreateLegacy();
            var legacy = fixture.LegacyPath;
            var original = ShellShortcut.Read(legacy);
            Assert.True(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            Assert.False(File.Exists(legacy));
            var path = fixture.ShortcutPath(fixture.Info);
            Assert.Equal(original, ShellShortcut.Read(path));
            var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, timestamp);

            // An older running version can recreate the legacy link after migration.
            fixture.CreateLegacy();
            Assert.True(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            Assert.False(File.Exists(legacy));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
            Assert.Single(Directory.GetFiles(fixture.Programs, "*.lnk", SearchOption.AllDirectories));
        });

    [Theory]
    [InlineData("identity")]
    [InlineData("target")]
    [InlineData("workingDirectory")]
    [InlineData("arguments")]
    [InlineData("invalid")]
    [InlineData("hotkey")]
    [InlineData("minimized")]
    [InlineData("maximized")]
    [InlineData("description")]
    [InlineData("runAsAdministrator")]
    [InlineData("iconPath")]
    [InlineData("iconIndex")]
    [InlineData("combined")]
    public void Registration_PreservesAlteredOrUnreadableLegacy(string change)
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            fixture.CreateLegacy();
            switch (change)
            {
                case "identity":
                    ShellShortcut.Save(fixture.LegacyPath, fixture.Info with { AppUserModelId = "Other.Application" });
                    break;
                case "target":
                    ShellShortcut.Save(fixture.LegacyPath, fixture.Info with { LauncherPath = Path.Combine(fixture.Root, "Other.exe") });
                    break;
                case "invalid":
                    File.WriteAllText(fixture.LegacyPath, "Not a Shell link");
                    break;
                default:
                    EditLegacy(fixture.LegacyPath, change, fixture.Root);
                    // The edit must exercise the metadata check rather than losing identity.
                    var edited = ShellShortcut.Read(fixture.LegacyPath);
                    Assert.Equal(fixture.Info.AppUserModelId, edited.AppUserModelId);
                    Assert.Equal(fixture.Info.LauncherPath, edited.Target);
                    switch (change)
                    {
                        case "hotkey": Assert.NotEqual((ushort)0, edited.Hotkey); break;
                        case "minimized": Assert.Equal(7, edited.ShowCommand); break;
                        case "maximized": Assert.Equal(3, edited.ShowCommand); break;
                        case "description": Assert.Equal("User description", edited.Description); break;
                        case "runAsAdministrator": Assert.True(edited.RunAsAdministrator); break;
                        case "iconPath": Assert.Equal(Path.Combine(fixture.Root, "custom.ico"), edited.IconPath); break;
                        case "iconIndex": Assert.Equal(1, edited.IconIndex); break;
                        case "combined":
                            Assert.NotEqual((ushort)0, edited.Hotkey);
                            Assert.Equal(7, edited.ShowCommand);
                            Assert.Equal("User description", edited.Description);
                            break;
                    }
                    break;
            }
            var before = File.ReadAllBytes(fixture.LegacyPath);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
                Assert.Equal(before, File.ReadAllBytes(fixture.LegacyPath));
            }
            var shortcuts = new SeasonalShortcutIconCoordinator(fixture.Info, fixture.Programs,
                Path.Combine(fixture.Root, "Cache"));
            foreach (var resource in new[] { SeasonalIcons.HalloweenIcon, SeasonalIcons.StandardIcon })
            {
                shortcuts.Refresh(resource);
                Assert.False(shortcuts.IsRegistered);
                Assert.Equal(before, File.ReadAllBytes(fixture.LegacyPath));
                var friendly = ShellShortcut.Read(fixture.ShortcutPath(fixture.Info));
                Assert.Equal(fixture.Info.AppUserModelId, friendly.AppUserModelId);
                Assert.Equal(fixture.Info.LauncherPath, friendly.Target);
                Assert.Equal((ushort)0, friendly.Hotkey);
                Assert.Equal(1, friendly.ShowCommand);
                Assert.Equal(ShellShortcut.DefaultDescription, friendly.Description);
                Assert.False(friendly.RunAsAdministrator);
            }
        });

    [Theory]
    [InlineData("workingDirectory")]
    [InlineData("arguments")]
    [InlineData("hotkey")]
    [InlineData("minimized")]
    [InlineData("maximized")]
    [InlineData("description")]
    [InlineData("runAsAdministrator")]
    [InlineData("iconPath")]
    [InlineData("iconIndex")]
    [InlineData("combined")]
    public void Registration_RestoredLegacyDefaultsAllowCleanupWithoutRewritingFriendlyLink(string change)
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            fixture.CreateLegacy();
            var original = ShellShortcut.Read(fixture.LegacyPath);
            EditLegacy(fixture.LegacyPath, change, fixture.Root);
            Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            var friendly = fixture.ShortcutPath(fixture.Info);
            var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(friendly, timestamp);
            EditLegacy(fixture.LegacyPath, change, fixture.Root, restore: true);
            Assert.Equal(original, ShellShortcut.Read(fixture.LegacyPath));
            Assert.True(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            Assert.False(File.Exists(fixture.LegacyPath));
            Assert.Equal(original, ShellShortcut.Read(friendly));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(friendly));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_UnprovenLegacyIconPreservesLinkAfterFriendlyCreation(bool readFails)
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            fixture.CreateLegacy();
            EditLegacy(fixture.LegacyPath, "iconPath", fixture.Root);
            var before = File.ReadAllBytes(fixture.LegacyPath);
            Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs,
                isManagedLegacyIcon: _ => readFails ? throw new IOException("Test ownership read failure") : false));
            Assert.Equal(before, File.ReadAllBytes(fixture.LegacyPath));
            Assert.Equal(fixture.Info.LauncherPath, ShellShortcut.Read(fixture.ShortcutPath(fixture.Info)).Target);
            // Callers without seasonal ownership context also retain an unknown icon.
            Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            Assert.Equal(before, File.ReadAllBytes(fixture.LegacyPath));
        });

    [Fact]
    public void Registration_LockedLegacyPreservesBothLinksAndRetriesCleanup()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            fixture.CreateLegacy();
            var before = File.ReadAllBytes(fixture.LegacyPath);
            using (File.Open(fixture.LegacyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
                Assert.Equal(before, File.ReadAllBytes(fixture.LegacyPath));
                Assert.Equal(fixture.Info.LauncherPath, ShellShortcut.Read(fixture.ShortcutPath(fixture.Info)).Target);
            }
            Assert.True(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            Assert.False(File.Exists(fixture.LegacyPath));
        });

    [Fact]
    public void Registration_FailedFriendlyCreationPreservesLegacy()
        => WpfTestHost.Run(() =>
        {
            using var fixture = new Deployment();
            fixture.CreateLegacy();
            var before = File.ReadAllBytes(fixture.LegacyPath);
            var path = fixture.ShortcutPath(fixture.Info);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(path); // Prevent the atomic move into the destination.
            Assert.False(SharedTaskbarShortcut.TryRegister(fixture.Info, fixture.Programs));
            Assert.Equal(before, File.ReadAllBytes(fixture.LegacyPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
        });

    private static void EditLegacy(string path, string change, string root, bool restore = false)
    {
        if (change == "runAsAdministrator")
        {
            EditNativeLegacy(path, runAsAdministrator: !restore);
            return;
        }
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        object? shortcut = null;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            shortcut = link;
            switch (change)
            {
                case "workingDirectory": link.WorkingDirectory = restore ? root : Path.Combine(root, "Other"); break;
                case "arguments": link.Arguments = restore ? string.Empty : "--user-added"; break;
                case "hotkey":
                    if (!restore)
                        link.Hotkey = "CTRL+ALT+W";
                    break;
                case "minimized": link.WindowStyle = restore ? 1 : 7; break;
                case "maximized": link.WindowStyle = restore ? 1 : 3; break;
                case "description": link.Description = restore ? ShellShortcut.DefaultDescription : "User description"; break;
                case "iconPath":
                    link.IconLocation = restore ? Path.Combine(root, "WeezTail.Launcher.exe") : Path.Combine(root, "custom.ico");
                    break;
                case "iconIndex":
                    link.IconLocation = Path.Combine(root, "WeezTail.Launcher.exe") + (restore ? ",0" : ",1");
                    break;
                case "combined":
                    if (!restore)
                        link.Hotkey = "CTRL+ALT+W";
                    link.WindowStyle = restore ? 1 : 7;
                    link.Description = restore ? ShellShortcut.DefaultDescription : "User description";
                    break;
                default: throw new ArgumentException("Unknown legacy customization.", nameof(change));
            }
            link.Save();
        }
        finally
        {
            if (shortcut != null)
                Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);
        }
        // WScript's empty Hotkey string can persist a nonzero virtual key.
        if (restore && change is "hotkey" or "combined")
            EditNativeLegacy(path, clearHotkey: true);
    }

    private static void EditNativeLegacy(string path, bool? runAsAdministrator = null, bool clearHotkey = false)
    {
        // Set metadata only: never execute the shortcut or request elevation.
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
        try
        {
            var file = (IPersistFile)instance;
            file.Load(path, 2); // STGM_READWRITE
            if (runAsAdministrator.HasValue)
            {
                var data = (IShellLinkDataList)instance;
                data.GetFlags(out var flags);
                data.SetFlags(runAsAdministrator.Value ? flags | 0x00002000u : flags & ~0x00002000u); // SLDF_RUNAS_USER
            }
            if (clearHotkey)
                ((IShellLinkW)instance).SetHotkey(0);
            file.Save(path, true);
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    // IShellLinkW vtable slots through SetHotkey; later methods are unused here.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
    }

    [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkDataList
    {
        void AddDataBlock(IntPtr dataBlock);
        void CopyDataBlock(uint signature, out IntPtr dataBlock);
        void RemoveDataBlock(uint signature);
        void GetFlags(out uint flags);
        void SetFlags(uint flags);
    }

    private sealed class Deployment : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "WeezTail taskbar é", Guid.NewGuid().ToString("N"));
        internal string Programs => Path.Combine(Root, "Programs");
        internal TaskbarRelaunchInfo Info => TaskbarRelaunchInfo.TryCreate(Path.Combine(Root, "WeezTail.Launcher.exe"),
            Path.Combine(Root, "releases", "1.2.3"))!;

        internal Deployment() => Directory.CreateDirectory(Root);
        internal string LegacyPath => Path.Combine(Programs, Info.LegacyShortcutRelativePath);
        internal void CreateLegacy()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
            ShellShortcut.Save(LegacyPath, Info);
        }
        internal string ShortcutPath(TaskbarRelaunchInfo info) => Path.Combine(Programs, info.ShortcutRelativePath);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
