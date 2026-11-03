namespace LogReader.Core.Tests.UncPerformance;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed record McpMeasurementResponse(JsonElement Envelope, double Milliseconds, int WireBytes,
    long WorkingSetBytes, long PrivateBytes)
{
    public JsonElement Result => Envelope.GetProperty("result");
}

// Sequential JSON-RPC client used only by the opt-in measurement process. The initialize
// response is the readiness signal. Never persist protocol bodies containing source text.
internal sealed class McpMeasurementClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<long> _stderr;
    private Task<string?>? _pendingRead;
    private int _requestId;
    private bool _stopped;

    private McpMeasurementClient(Process process)
    {
        _process = process;
        _stderr = DrainErrorAsync(process.StandardError);
    }

    public static McpMeasurementClient Start(string executable)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Measurement child did not start.");
            return new(process);
        }
        catch { process.Dispose(); throw; }
    }

    public async Task<double> InitializeAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var response = await RequestAsync("initialize", new
        {
            protocolVersion = "2025-11-25", capabilities = new { },
            clientInfo = new { name = "weeztail-unc-measurement", version = "1" }
        }, ct);
        if (response.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() != "weeztail")
            throw new InvalidOperationException("Unexpected measurement server.");
        await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } }, ct);
        return watch.Elapsed.TotalMilliseconds;
    }

    public async Task<JsonElement> ListToolsAsync(CancellationToken ct)
        => (await RequestAsync("tools/list", new { }, ct)).GetProperty("result").Clone();

    public async Task<McpMeasurementResponse> CallAsync(string name, object arguments, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var wire = await RequestAsync("tools/call", new { name, arguments }, ct);
        var envelope = ValidateToolResponse(wire.GetProperty("result"));
        var elapsed = watch.Elapsed.TotalMilliseconds;
        _process.Refresh();
        return new(envelope, elapsed, Encoding.UTF8.GetByteCount(wire.GetRawText()),
            _process.WorkingSet64, _process.PrivateMemorySize64);
    }

    internal static JsonElement ValidateToolResponse(JsonElement result)
    {
        if (result.TryGetProperty("isError", out var error) && error.GetBoolean())
            throw new InvalidOperationException("Measurement tool returned an error.");
        var structured = result.GetProperty("structuredContent");
        var texts = result.GetProperty("content").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "text").ToArray();
        if (texts.Length != 1 || !JsonNode.DeepEquals(JsonNode.Parse(texts[0].GetProperty("text").GetString()!),
                JsonNode.Parse(structured.GetRawText())))
            throw new InvalidOperationException("MCP structured/text parity failed.");
        if (structured.TryGetProperty("errors", out var errors) && errors.GetArrayLength() != 0)
            throw new InvalidOperationException("Measurement envelope contains errors.");
        return structured.Clone();
    }

    public async Task<double> StopAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        _process.StandardInput.Close();
        await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        _stopped = true;
        if (_process.ExitCode != 0 || await _stderr != 0)
            throw new InvalidOperationException("Measurement server shutdown or stderr was not clean.");
        return watch.Elapsed.TotalMilliseconds;
    }

    private async Task<JsonElement> RequestAsync(string method, object arguments, CancellationToken ct)
    {
        var id = ++_requestId;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = arguments }, ct);
        while (true)
        {
            _pendingRead = _process.StandardOutput.ReadLineAsync();
            var line = await _pendingRead.WaitAsync(TimeSpan.FromSeconds(35), ct);
            _pendingRead = null;
            if (line == null) throw new IOException("Measurement server stdout closed early.");
            if (line.Length > 4 * 1024 * 1024)
                throw new InvalidOperationException("Measurement response exceeded its wire bound.");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0")
                throw new InvalidOperationException("Non-protocol measurement stdout.");
            if (!root.TryGetProperty("id", out var responseId)) continue;
            if (responseId.GetInt32() != id || root.TryGetProperty("error", out _))
                throw new InvalidOperationException("Unexpected JSON-RPC measurement response.");
            return root.Clone();
        }
    }

    private async Task SendAsync(object message, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }

    private static async Task<long> DrainErrorAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        long count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0) count += read;
        return count; // Count only; source paths/text never enter a diagnostic report.
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_stopped)
            {
                _process.StandardInput.Close();
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            if (_pendingRead != null)
                await _pendingRead.WaitAsync(TimeSpan.FromSeconds(5));
            await _stderr.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { _process.Dispose(); }
    }
}
