namespace LogReader.Infrastructure.Services;

using System.Diagnostics;
using System.Text;

internal sealed record GitProcessResult(int ExitCode, byte[] Output, string Error);

internal interface IGitProcessRunner
{
    Task<GitProcessResult> RunAsync(string directory, IReadOnlyList<string> arguments, int outputLimit, CancellationToken cancellationToken);
}

internal sealed class GitProcessRunner : IGitProcessRunner
{
    private readonly string _executable;
    public GitProcessRunner() : this("git") { }
    internal GitProcessRunner(string executable) => _executable = executable;
    public async Task<GitProcessResult> RunAsync(string directory, IReadOnlyList<string> arguments, int outputLimit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_SSH_COMMAND"] = "ssh -oBatchMode=yes";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { throw new IOException("Git could not be started. Install Git for Windows and ensure git is on PATH.", ex); }
            process.StandardInput.Close();
            var stdout = ReadAsync(process.StandardOutput.BaseStream, outputLimit, timeout.Token);
            var stderr = ReadAsync(process.StandardError.BaseStream, 8192, timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (stdout.Result.Exceeded) throw new InvalidDataException("Git returned more data than the view-source limit permits.");
            return new(process.ExitCode, stdout.Result.Bytes, Encoding.UTF8.GetString(stderr.Result.Bytes));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Git timed out after two minutes. Check connectivity and authentication, then retry.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<(byte[] Bytes, bool Exceeded)> ReadAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        var exceeded = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            var take = Math.Min(count, limit - (int)output.Length);
            output.Write(buffer, 0, take);
            exceeded |= take != count;
        }
        return (output.ToArray(), exceeded);
    }
}
