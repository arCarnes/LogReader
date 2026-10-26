namespace LogReader.App.Services;

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

internal sealed class SeasonalShortcutIconCoordinator
{
    private readonly TaskbarRelaunchInfo _info;
    private readonly string _localApplicationData;
    private readonly Func<string, byte[]> _readIcon;
    private readonly Func<string, bool> _registerShortcut;
    private readonly Dictionary<string, byte[]> _icons = new(StringComparer.Ordinal);

    internal SeasonalShortcutIconCoordinator(TaskbarRelaunchInfo info, string? programsDirectory = null,
        string? localApplicationData = null, Func<string, byte[]>? readIcon = null,
        Func<string, bool>? registerShortcut = null)
    {
        _info = info;
        _localApplicationData = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _readIcon = readIcon ?? ReadIcon;
        _registerShortcut = registerShortcut ?? (path => SharedTaskbarShortcut.TryRegister(info, programsDirectory, path));
    }

    internal bool IsRegistered { get; private set; }

    // The same user's Start menu/cache can be shared by multiple logon sessions.
    private string MutexName => @"Global\WeezTail.ShortcutIcon." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName + "|" + _info.AppUserModelId)));

    internal void Refresh(string resource)
    {
        try
        {
            using var mutex = new Mutex(false, MutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                // Another instance owns the update. Retry on the next existing refresh.
                if (!acquired)
                    return;

                var iconPath = _info.LauncherPath;
                if (resource != SeasonalIcons.StandardIcon)
                {
                    try
                    {
                        iconPath = EnsureIcon(resource);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        Trace.TraceWarning("Could not prepare the seasonal shortcut icon; using the launcher icon (HRESULT 0x{0:X8}).", ex.HResult);
                    }
                }
                IsRegistered = _registerShortcut(iconPath);
            }
            finally
            {
                if (acquired)
                    mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning("Could not refresh the seasonal shortcut icon (HRESULT 0x{0:X8}).", ex.HResult);
        }
    }

    private string EnsureIcon(string resource)
    {
        if (string.IsNullOrWhiteSpace(_localApplicationData) || !Path.IsPathFullyQualified(_localApplicationData))
            throw new IOException("The current user's local application data directory is unavailable.");
        if (!_icons.TryGetValue(resource, out var bytes))
        {
            bytes = _readIcon(resource);
            _icons.Add(resource, bytes);
        }
        var directory = Path.Combine(_localApplicationData, "WeezTail", "ShellIcons", Hash(Encoding.UTF8.GetBytes(_info.AppUserModelId)));
        var path = Path.Combine(directory, Hash(bytes) + ".ico");
        // IShellLink truncates icon locations to MAX_PATH even with a larger read buffer.
        if (path.Length >= 260)
            throw new IOException("The seasonal shortcut icon path exceeds the Shell limit.");
        if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes))
            return path;

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return path;
    }

    private static byte[] ReadIcon(string resource)
    {
        using var stream = Application.GetResourceStream(new Uri(resource, UriKind.Absolute))?.Stream
            ?? throw new FileNotFoundException("The bundled seasonal icon was not found.", resource);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Hash(byte[] bytes)
        => Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
