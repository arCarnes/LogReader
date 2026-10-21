namespace LogReader.App.Services;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

internal static class SharedTaskbarShortcut
{
    internal static bool TryRegister(TaskbarRelaunchInfo info, string? programsDirectory = null)
    {
        try
        {
            var programs = programsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrWhiteSpace(programs) || !Path.IsPathFullyQualified(programs))
                throw new IOException("The current user's Start menu Programs directory is unavailable.");
            var directory = Path.Combine(programs, "WeezTail Shared");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, info.ShortcutFileName);
            var replacing = File.Exists(destination);
            if (replacing)
            {
                var existing = ShellShortcut.Read(destination);
                if (existing.AppUserModelId != info.AppUserModelId ||
                    !string.Equals(existing.Target, info.LauncherPath, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.TraceWarning("WeezTail taskbar shortcut destination belongs to another shortcut; leaving it unchanged.");
                    return false;
                }
            }

            // Save a complete link before replacing our own destination. A failed save
            // must not destroy a working shortcut, and a new link must not overwrite one.
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".lnk");
            try
            {
                ShellShortcut.Save(temporary, info);
                if (replacing)
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                File.Delete(temporary);
            }
            // Notify the Shell of the final name before assigning the window identity.
            // SHCNE_UPDATEITEM / SHCNE_CREATE, with SHCNF_PATHW | SHCNF_FLUSH.
            SHChangeNotify(replacing ? 0x00002000 : 0x00000002, 0x0005 | 0x1000, destination, IntPtr.Zero);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or
            InvalidCastException or ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning("Could not register WeezTail taskbar shortcut; using window relaunch properties (HRESULT 0x{0:X8}).", ex.HResult);
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags,
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr secondItem);
}

internal sealed record ShellShortcutInfo(
    string Target, string WorkingDirectory, string Arguments, string IconPath, int IconIndex, string? AppUserModelId);

// Never resolve or execute links here. The caller owns only its generated .lnk file,
// and all interfaces below share a single COM object released in the finally block.
internal static class ShellShortcut
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    internal static void Save(string path, TaskbarRelaunchInfo info)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass, throwOnError: true)!)!;
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(info.LauncherPath);
            link.SetWorkingDirectory(info.DeploymentRoot);
            link.SetArguments(string.Empty);
            link.SetDescription("WeezTail shared deployment");
            link.SetIconLocation(info.LauncherPath, 0);
            link.SetShowCmd(1); // SW_SHOWNORMAL
            using var properties = new ShellPropertyStore(instance);
            properties.SetString(5, info.AppUserModelId);
            properties.Commit();
            ((IPersistFile)instance).Save(path, true);
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    internal static ShellShortcutInfo Read(string path)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass, throwOnError: true)!)!;
        try
        {
            ((IPersistFile)instance).Load(path, 0); // STGM_READ; no Resolve call.
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            var workingDirectory = new StringBuilder(32768);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            var arguments = new StringBuilder(32768);
            link.GetArguments(arguments, arguments.Capacity);
            var icon = new StringBuilder(32768);
            link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            using var properties = new ShellPropertyStore(instance);
            return new ShellShortcutInfo(target.ToString(), workingDirectory.ToString(), arguments.ToString(),
                icon.ToString(), iconIndex, properties.GetString(5));
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    // IShellLinkW vtable order from ShObjIdl_core.h, including unused slots.
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
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
