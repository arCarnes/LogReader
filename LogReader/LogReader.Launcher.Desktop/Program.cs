namespace LogReader.Launcher.Desktop;

using System.Diagnostics;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => Run(args, AppContext.BaseDirectory,
            (message, title) => MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error),
            Process.Start);

    internal static int Run(string[] args, string launcherDirectory,
        Action<string, string> reportFailure, Func<ProcessStartInfo, Process?> startProcess)
    {
        try
        {
            var target = ReleaseResolver.Resolve(launcherDirectory);
            var startInfo = target.CreateStartInfo(args);
            startInfo.Environment["WEEZTAIL_DESKTOP_LAUNCHER_PATH"] =
                Path.GetFullPath(Path.Combine(launcherDirectory, "WeezTail.Launcher.exe"));
            using var process = startProcess(startInfo);
            if (process == null)
                throw new InvalidOperationException($"Could not start '{target.ExecutablePath}'.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Text.Json.JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            reportFailure(
                $"WeezTail could not launch from:{Environment.NewLine}{launcherDirectory}" +
                $"{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "WeezTail Launcher");
            return 1;
        }
    }
}
