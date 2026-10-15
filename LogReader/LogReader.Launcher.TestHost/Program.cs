namespace LogReader.Launcher.TestHost;

using System.Text.Json;

// Standalone child-process fixture. It is never included in release packages.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "started.json"),
            JsonSerializer.Serialize(new { pid = Environment.ProcessId, cwd = Environment.CurrentDirectory, args }));
        var mode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "mode.txt"));
        if (mode == "earlyExit")
            return 37;
        if (mode == "wait")
            await Task.Delay(Timeout.Infinite);
        if (mode == "desktop")
        {
            while (!File.Exists(Path.Combine(AppContext.BaseDirectory, "stop.txt")))
                await Task.Delay(25);
            return 0;
        }
        if (mode == "flood")
        {
            var bytes = Enumerable.Range(0, 131072).Select(i => (byte)i).ToArray();
            await Task.WhenAll(
                Console.OpenStandardOutput().WriteAsync(bytes).AsTask(),
                Console.OpenStandardError().WriteAsync(bytes).AsTask());
        }
        await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
        return 0;
    }
}
