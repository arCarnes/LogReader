namespace LogReader.App.Services;

using System.IO;

public enum DashboardFileProbeStatus
{
    Found,
    Missing,
    AccessDenied,
    InvalidPath,
    Unavailable
}

public readonly record struct DashboardFileProbeResult(DashboardFileProbeStatus Status)
{
    public bool IsFound => Status == DashboardFileProbeStatus.Found;

    public string? ErrorMessage => Status switch
    {
        DashboardFileProbeStatus.Missing => "File not found",
        DashboardFileProbeStatus.AccessDenied => "File unavailable: access denied",
        DashboardFileProbeStatus.InvalidPath => "File path is invalid",
        DashboardFileProbeStatus.Unavailable => "File unavailable",
        _ => null
    };

    public static DashboardFileProbeResult Found { get; } = new(DashboardFileProbeStatus.Found);

    public static DashboardFileProbeResult Missing { get; } = new(DashboardFileProbeStatus.Missing);

    public static DashboardFileProbeResult AccessDenied { get; } = new(DashboardFileProbeStatus.AccessDenied);

    public static DashboardFileProbeResult InvalidPath { get; } = new(DashboardFileProbeStatus.InvalidPath);

    public static DashboardFileProbeResult Unavailable { get; } = new(DashboardFileProbeStatus.Unavailable);
}

internal static class DashboardFileProbe
{
    public static Task<DashboardFileProbeResult> ProbeOffUiAsync(string filePath, CancellationToken ct)
        => Task.Run(() => Probe(filePath), ct).WaitAsync(ct);

    public static DashboardFileProbeResult Probe(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return DashboardFileProbeResult.InvalidPath;

        try
        {
            var attributes = File.GetAttributes(filePath);
            return attributes.HasFlag(FileAttributes.Directory)
                ? DashboardFileProbeResult.Missing
                : DashboardFileProbeResult.Found;
        }
        catch (FileNotFoundException)
        {
            return DashboardFileProbeResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return DashboardFileProbeResult.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return DashboardFileProbeResult.AccessDenied;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DashboardFileProbeResult.InvalidPath;
        }
        catch (IOException)
        {
            return DashboardFileProbeResult.Unavailable;
        }
    }
}
