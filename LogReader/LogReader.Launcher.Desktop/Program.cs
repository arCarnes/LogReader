namespace LogReader.Launcher.Desktop;

using System.Diagnostics;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var target = ReleaseResolver.Resolve(AppContext.BaseDirectory);
            using var process = Process.Start(target.CreateStartInfo(args));
            if (process == null)
                throw new InvalidOperationException($"Could not start '{target.ExecutablePath}'.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                $"WeezTail could not launch from:{Environment.NewLine}{AppContext.BaseDirectory}" +
                $"{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "WeezTail Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
