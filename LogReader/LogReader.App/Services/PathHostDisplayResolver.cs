namespace LogReader.App.Services;

using System.Runtime.InteropServices;
using System.Text;

internal interface IMappedDriveConnectionResolver
{
    bool TryGetRemotePath(string driveName, out string remotePath);
}

internal sealed class PathHostDisplayResolver
{
    public static PathHostDisplayResolver Shared { get; } = new(new WindowsMappedDriveConnectionResolver());

    private readonly IMappedDriveConnectionResolver _mappedDriveResolver;
    private readonly Dictionary<string, string?> _mappedDriveHosts = new(StringComparer.OrdinalIgnoreCase);

    public PathHostDisplayResolver(IMappedDriveConnectionResolver mappedDriveResolver)
    {
        _mappedDriveResolver = mappedDriveResolver;
    }

    public string? CreateHostNameText(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        if (TryGetUncHost(filePath, out var uncHost))
            return uncHost;

        if (!TryGetDriveName(filePath, out var driveName))
            return null;

        var mappedHost = GetMappedDriveHost(driveName);
        return mappedHost ?? driveName.ToUpperInvariant();
    }

    private string? GetMappedDriveHost(string driveName)
    {
        lock (_mappedDriveHosts)
        {
            if (_mappedDriveHosts.TryGetValue(driveName, out var cachedHost))
                return cachedHost;
        }

        string? host = null;
        if (_mappedDriveResolver.TryGetRemotePath(driveName, out var remotePath) &&
            TryGetUncHost(remotePath, out var remoteHost))
        {
            host = remoteHost;
        }

        lock (_mappedDriveHosts)
        {
            if (!_mappedDriveHosts.ContainsKey(driveName))
                _mappedDriveHosts[driveName] = host;

            return _mappedDriveHosts[driveName];
        }
    }

    private static bool TryGetDriveName(string filePath, out string driveName)
    {
        if (filePath.Length >= 3 &&
            filePath[1] == ':' &&
            char.IsAsciiLetter(filePath[0]) &&
            (filePath[2] == '\\' || filePath[2] == '/'))
        {
            driveName = filePath[..2];
            return true;
        }

        driveName = string.Empty;
        return false;
    }

    private static bool TryGetUncHost(string path, out string host)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            host = string.Empty;
            return false;
        }

        var trimmed = path.TrimStart('\\');
        var separator = trimmed.IndexOf('\\', StringComparison.Ordinal);
        if (separator <= 0)
        {
            host = string.Empty;
            return false;
        }

        host = trimmed[..separator];
        return true;
    }
}

internal sealed class WindowsMappedDriveConnectionResolver : IMappedDriveConnectionResolver
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;

    public bool TryGetRemotePath(string driveName, out string remotePath)
    {
        var capacity = 512;
        var buffer = new StringBuilder(capacity);
        var result = WNetGetConnection(driveName, buffer, ref capacity);
        if (result == ErrorMoreData)
        {
            buffer = new StringBuilder(capacity);
            result = WNetGetConnection(driveName, buffer, ref capacity);
        }

        if (result == NoError)
        {
            remotePath = buffer.ToString();
            return !string.IsNullOrWhiteSpace(remotePath);
        }

        remotePath = string.Empty;
        return false;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
