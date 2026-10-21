namespace LogReader.Launcher.Mcp;

using System.Diagnostics;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            Console.Error.WriteLine("WeezTail MCP launcher does not accept command-line arguments.");
            return 2;
        }
        try
        {
            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"WeezTail MCP could not launch from '{AppContext.BaseDirectory}': {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        var target = ReleaseResolver.Resolve(AppContext.BaseDirectory, mcp: true);
        ProcessLifetimeJob.Initialize();
        var startInfo = target.CreateStartInfo([]);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{target.ExecutablePath}'.");
        var childInput = process.StandardInput;
        var exit = process.WaitForExitAsync();
        var output = Task.Run(() => RelayAsync(process.StandardOutput.BaseStream, Console.OpenStandardOutput()));
        var error = Task.Run(() => RelayAsync(process.StandardError.BaseStream, Console.OpenStandardError()));
        var input = Task.Run(async () =>
        {
            try
            {
                await RelayAsync(Console.OpenStandardInput(), childInput.BaseStream);
            }
            catch (IOException) when (process.HasExited)
            {
                // A child may exit before the client closes its input pipe.
            }
            finally
            {
                childInput.Close();
            }
        });

        try
        {
            var pending = new List<Task> { exit, input, output, error };
            Task? shutdownDeadline = null;
            while (!exit.IsCompleted)
            {
                var completed = await Task.WhenAny(pending);
                if (completed == shutdownDeadline && !exit.IsCompleted)
                {
                    process.Kill(entireProcessTree: true);
                    await exit;
                    await Task.WhenAll(output, error);
                    Console.Error.WriteLine("WeezTail MCP did not stop within five seconds after stdin closed.");
                    return 1;
                }
                await completed;
                pending.Remove(completed);
                if (completed == input)
                {
                    shutdownDeadline = Task.Delay(TimeSpan.FromSeconds(5));
                    pending.Add(shutdownDeadline);
                }
            }
            await exit;
            await Task.WhenAll(output, error);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task RelayAsync(Stream source, Stream destination)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read));
            await destination.FlushAsync();
        }
    }
}
