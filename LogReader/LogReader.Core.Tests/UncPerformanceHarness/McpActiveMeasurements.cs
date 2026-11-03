namespace LogReader.Core.Tests.UncPerformance;

using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static partial class McpWorkflowMeasurements
{
    private static async Task<Sample> MeasureActiveAsync(McpMeasurementClient client,
        (string Id, string Path)[] files, bool matching, CancellationToken ct)
    {
        foreach (var file in files)
        {
            FixtureStore.RequireLocalPath(file.Path); // Mutate only files generated in this request's isolated local deployment.
            await File.WriteAllTextAsync(file.Path, "old\n", ct);
        }
        string? cursor = null;
        var calls = 0;
        long responseBytes = 0, working = 0, privateBytes = 0;
        var identities = new List<string>();
        var progress = new List<Progress>();
        Sample? sample = null;
        try
        {
            var initial = await client.CallAsync("monitor_logs", new
            {
                targets = new[] { new { kind = "dashboard", id = $"local-active-{files.Length}" } }, query = "needle"
            }, ct);
            cursor = OptionalString(initial.Result, "nextCursor");
            RejectIncompleteEnvelope(initial);
            RejectFileErrors(initial.Result);
            if (cursor == null || initial.Result.GetProperty("selectedFileCount").GetInt32() != files.Length)
                throw new InvalidOperationException("Active baseline was incomplete.");
            for (var i = 0; i < files.Length; i++)
                await File.AppendAllTextAsync(files[i].Path, matching ? $"needle {i:D3}\n" : "other\n", ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var poll = await client.CallAsync("monitor_logs", new { cursor, maxFiles = 10, timeoutMilliseconds = 30000 }, ct);
                calls++;
                responseBytes += poll.WireBytes;
                working = Math.Max(working, poll.WorkingSetBytes);
                privateBytes = Math.Max(privateBytes, poll.PrivateBytes);
                var result = poll.Result;
                progress.Add(GetProgress(result));
                cursor = OptionalString(result, "nextCursor");
                RejectIncompleteEnvelope(poll);
                RejectFileErrors(result);
                var batch = result.GetProperty("files").EnumerateArray().ToArray();
                if (cursor == null || batch.Length != result.GetProperty("checkedFileCount").GetInt32() ||
                    result.GetProperty("backlogFileCount").GetInt32() != 0 || result.GetProperty("stoppedFileCount").GetInt32() != 0 ||
                    result.GetProperty("isIdle").GetBoolean() || result.GetProperty("bytesEvaluated").GetInt64() > 4L * 1024 * 1024)
                    throw new InvalidOperationException("Active sweep lost progress, exceeded budgets or falsely reported idle.");
                foreach (var file in batch)
                {
                    var id = file.GetProperty("fileId").GetString()!;
                    if (!seen.Add(id)) throw new InvalidOperationException("Active sweep duplicated a slot.");
                    var index = Array.FindIndex(files, f => f.Id == id);
                    if (index < 0) throw new InvalidOperationException("Active sweep returned an unexpected file.");
                    var events = file.GetProperty("events").EnumerateArray().ToArray();
                    if (events.Length != (matching ? 1 : 0))
                        throw new InvalidOperationException("Active event count differed.");
                    if (matching)
                    {
                        var item = events[0];
                        if (file.GetProperty("generation").ValueKind != JsonValueKind.String ||
                            item.GetProperty("kind").GetString() != "appended" || item.GetProperty("byteOffset").GetInt64() != 4 ||
                            item.GetProperty("text").GetString() != $"needle {index:D3}" || item.GetProperty("isTruncated").GetBoolean())
                            throw new InvalidOperationException("Active event identity/content differed.");
                    }
                    // Physical generation differs between independent local deployments. Validate its
                    // presence above, but normalize reports to deterministic file/offset/kind/content.
                    identities.Add($"{id}:4:{(matching ? $"appended:needle {index:D3}" : "nonmatch")}");
                }
                var complete = result.GetProperty("isScopeChecked").GetBoolean();
                if (result.GetProperty("uncheckedFileCount").GetInt32() != files.Length - seen.Count ||
                    result.GetProperty("isCaughtUp").GetBoolean() != complete)
                    throw new InvalidOperationException("Active scope fields were not truthful.");
                if (complete)
                {
                    watch.Stop();
                    if (seen.Count != files.Length) throw new InvalidOperationException("Active sweep did not check every file.");
                    sample = new($"local-active-{files.Length}", matching ? "monitor-match" : "monitor-nonmatch", files.Length,
                        files.Sum(file => new FileInfo(file.Path).Length), watch.Elapsed.TotalMilliseconds, matching ? files.Length : 0,
                        Hash(Encoding.UTF8.GetBytes(string.Join('\n', identities))), calls, responseBytes, null, null, working, privateBytes,
                        progress.ToArray());
                    break;
                }
                if (calls > files.Length + 1) throw new InvalidOperationException("Active sweep did not converge.");
            }
        }
        finally
        {
            if (cursor != null)
            {
                var release = await client.CallAsync("monitor_logs", new { cursor, release = true }, ct);
                if (release.Result.GetProperty("state").GetString() != "released")
                    throw new InvalidOperationException("Active monitor release was not terminal.");
                if (sample != null) sample = sample with { ReleaseMilliseconds = release.Milliseconds };
            }
        }
        return sample ?? throw new InvalidOperationException("Active sweep produced no complete measurement.");
    }
}
