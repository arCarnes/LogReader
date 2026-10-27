namespace LogReader.App.Services;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;

internal sealed record TaskbarRelaunchInfo(string LauncherPath, string AppUserModelId)
{
    internal const string EnvironmentVariable = "WEEZTAIL_DESKTOP_LAUNCHER_PATH";
    public string Command => $"\"{LauncherPath}\"";
    public string Icon => $"{LauncherPath},0";
    public string DeploymentRoot => Path.GetDirectoryName(LauncherPath)!;
    public string DeploymentHash => AppUserModelId["WeezTail.Shared.".Length..];
    public string ShortcutFileName => "WeezTail.lnk";
    public string ShortcutRelativePath => Path.Combine("WeezTail Shared", DeploymentHash, ShortcutFileName);
    public string LegacyShortcutRelativePath => Path.Combine("WeezTail Shared", $"WeezTail ({DeploymentHash}).lnk");

    internal static TaskbarRelaunchInfo? ConsumeEnvironment()
        => ConsumeEnvironment(
            () => Environment.GetEnvironmentVariable(EnvironmentVariable),
            () => Environment.SetEnvironmentVariable(EnvironmentVariable, null),
            AppContext.BaseDirectory);

    internal static TaskbarRelaunchInfo? ConsumeEnvironment(Func<string?> read, Action clear, string appDirectory,
        Func<string, bool>? fileExists = null, Func<string, string>? readText = null)
    {
        var path = read();
        clear();
        var discovered = Discover(appDirectory, fileExists, readText);
        if (!string.IsNullOrWhiteSpace(path) &&
            TryCreate(path, appDirectory)?.AppUserModelId != discovered?.AppUserModelId)
            Trace.TraceWarning("Ignoring unrelated WeezTail taskbar launcher handoff.");
        return discovered;
    }

    internal static TaskbarRelaunchInfo? Discover(string appDirectory,
        Func<string, bool>? fileExists = null, Func<string, string>? readText = null)
    {
        try
        {
            var root = new DirectoryInfo(Path.GetFullPath(appDirectory)).Parent?.Parent;
            if (root == null)
                return null;
            var info = TryCreate(Path.Combine(root.FullName, "WeezTail.Launcher.exe"), appDirectory);
            if (info == null)
                return null;
            var exists = fileExists ?? File.Exists;
            if (!exists(Path.Combine(root.FullName, "current.json")) || !exists(info.LauncherPath))
            {
                Trace.TraceWarning("Shared WeezTail taskbar discovery requires the release pointer and desktop launcher.");
                return null;
            }
            using var config = JsonDocument.Parse((readText ?? File.ReadAllText)(
                Path.Combine(appDirectory, "WeezTail.install.json")));
            if (config.RootElement.ValueKind != JsonValueKind.Object ||
                !config.RootElement.TryGetProperty("installMode", out var installMode) ||
                installMode.ValueKind != JsonValueKind.String || installMode.GetString() != "Portable" ||
                !config.RootElement.TryGetProperty("storageMode", out var storageMode) ||
                storageMode.ValueKind != JsonValueKind.String || storageMode.GetString() != "LocalAppData")
            {
                Trace.TraceWarning("Shared WeezTail taskbar discovery requires Portable/LocalAppData configuration.");
                return null;
            }
            return info;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or
            UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not discover shared WeezTail taskbar deployment (HRESULT 0x{0:X8}).", ex.HResult);
            return null;
        }
    }

    internal static TaskbarRelaunchInfo? TryCreate(string? launcherPath, string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;
        try
        {
            if (!Path.IsPathFullyQualified(launcherPath) || launcherPath.Contains('"'))
                return null;
            var launcher = Path.GetFullPath(launcherPath);
            var release = new DirectoryInfo(Path.GetFullPath(appDirectory));
            var root = release.Parent?.Parent;
            if (root == null ||
                !Regex.IsMatch(release.Name, @"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant) ||
                !Version.TryParse(release.Name, out _) ||
                !string.Equals(release.Parent!.Name, "releases", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(launcher, Path.Combine(root.FullName, "WeezTail.Launcher.exe"), StringComparison.OrdinalIgnoreCase))
                return null;

            // Deployment identity must survive promotion without merging unrelated deployments.
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.FullName.ToUpperInvariant())));
            return new TaskbarRelaunchInfo(launcher, "WeezTail.Shared." + identity);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not read WeezTail taskbar relaunch metadata (HRESULT 0x{0:X8}).", ex.HResult);
            return null;
        }
    }
}

internal static class TaskbarRelaunchProperties
{
    internal static void Apply(Window window, TaskbarRelaunchInfo info, bool shortcutRegistered = false)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            // WM_DESTROY still has a valid HWND and runs only for an actual close,
            // unlike Closing, which can be canceled by the shutdown coordinator.
            HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);
            using var properties = new ShellPropertyStore(handle);
            // A registered shortcut supplies relaunch information. If registration failed,
            // set the complete fallback description before assigning the window identity.
            if (!shortcutRegistered)
            {
                properties.SetString(2, info.Command);
                properties.SetString(4, "WeezTail");
                properties.SetString(3, info.Icon);
            }
            properties.SetString(5, info.AppUserModelId);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidCastException or ArgumentException)
        {
            Trace.TraceWarning("Could not configure WeezTail taskbar relaunch (HRESULT 0x{0:X8}).", ex.HResult);
        }
    }

    private static IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0002) // WM_DESTROY
            Clear(window);
        return IntPtr.Zero;
    }

    internal static void Clear(IntPtr window)
    {
        try
        {
            using var properties = new ShellPropertyStore(window);
            foreach (var id in new uint[] { 5, 2, 4, 3 })
                properties.Clear(id);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidCastException or ArgumentException)
        {
            Trace.TraceWarning("Could not clear WeezTail taskbar relaunch properties (HRESULT 0x{0:X8}).", ex.HResult);
        }
    }
}

// Property stores belong to windows or Shell links. Each PROPVARIANT is ours;
// release the RCW only when this adapter acquired it through the window API.
internal sealed class ShellPropertyStore : IDisposable
{
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private readonly IPropertyStore _store;
    private readonly bool _ownsStore;

    internal ShellPropertyStore(IntPtr window)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out _store));
        _ownsStore = true;
    }

    // The Shell link owner releases the shared RCW after all its interfaces are used.
    internal ShellPropertyStore(object shellLink) => _store = (IPropertyStore)shellLink;

    internal void Commit() => _store.Commit();

    internal void SetString(uint id, string value)
    {
        var key = new PropertyKey { Format = AppUserModelFormat, Id = id };
        var variant = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) }; // VT_LPWSTR
        try
        {
            _store.SetValue(ref key, ref variant);
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    internal string? GetString(uint id)
    {
        var key = new PropertyKey { Format = AppUserModelFormat, Id = id };
        var variant = new PropVariant();
        try
        {
            _store.GetValue(ref key, out variant);
            return variant.Type == 31 ? Marshal.PtrToStringUni(variant.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    internal void Clear(uint id)
    {
        var key = new PropertyKey { Format = AppUserModelFormat, Id = id };
        var empty = new PropVariant(); // VT_EMPTY releases the window's stored value.
        _store.SetValue(ref key, ref empty);
    }

    public void Dispose()
    {
        if (_ownsStore)
            Marshal.ReleaseComObject(_store);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        internal Guid Format;
        internal uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        internal ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        internal IntPtr Pointer;
        private IntPtr _unionPadding;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);
}
