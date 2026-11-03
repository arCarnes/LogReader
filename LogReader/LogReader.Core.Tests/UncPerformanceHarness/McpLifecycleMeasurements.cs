namespace LogReader.Core.Tests.UncPerformance;

using System.Text;
using System.Text.Json;

internal static partial class McpWorkflowMeasurements
{
    private static IEnumerable<string> LifecycleScenarios()
    {
        foreach (var character in new[] { "é", "€", "😀" })
            for (var split = 1; split < Encoding.UTF8.GetByteCount(character); split++)
                foreach (var bom in new[] { false, true })
                    yield return $"utf8-{Convert.ToHexString(Encoding.UTF8.GetBytes(character))}-{split}-{bom}";
        foreach (var scenario in new[] { "utf16-half", "ansi", "unfinished", "crlf", "replace", "truncate",
            "encoding", "missing-reappear", "handoff-search", "handoff-count" })
            yield return scenario;
    }

    private static async Task<Sample> MeasureLifecycleAsync(McpMeasurementClient client,
        (string Id, string Path)[] ownedFiles, string scenario, CancellationToken ct)
    {
        // Only mutate generated deployment files, never manifest sources. The second file proves
        // healthy progress when the first changes or becomes temporarily unavailable.
        foreach (var file in ownedFiles) FixtureStore.RequireLocalPath(file.Path);
        var files = ownedFiles.Take(scenario is "replace" or "truncate" or "encoding" or "missing-reappear" ? 2 : 1).ToArray();
        var path = files[0].Path;
        var seed = scenario == "unfinished" ? "" : scenario == "crlf" ? "old\r" :
            scenario is "replace" or "truncate" or "encoding" ? "long old content\n" : "old\n";
        var utf8 = scenario.StartsWith("utf8-", StringComparison.Ordinal) ? scenario.Split('-') : null;
        var encoding = scenario == "utf16-half" ? Encoding.Unicode :
            utf8 != null && bool.Parse(utf8[3]) ? new UTF8Encoding(true) : new UTF8Encoding(false);
        await File.WriteAllTextAsync(path, seed, encoding, ct);
        if (scenario == "ansi") await File.WriteAllBytesAsync(path, [0xE9, 10], ct);
        if (files.Length == 2) await File.WriteAllTextAsync(files[1].Path, "old\n", ct);
        var offset = new FileInfo(path).Length;
        var sourceBytes = files.Sum(file => new FileInfo(file.Path).Length);
        var targets = files.Select(file => new { kind = "logFile", id = file.Id }).ToArray();
        string? cursor = null;
        var progress = new List<Progress>();
        var identities = new List<string>();
        var generations = new Dictionary<string, string>(StringComparer.Ordinal);
        long units = 0, wire = 0, working = 0, privateBytes = 0, bytes = 0;
        double elapsed = 0, releaseMilliseconds = 0;
        var calls = 0;
        var backup = path + ".missing";
        try
        {
            if (scenario.StartsWith("handoff-", StringComparison.Ordinal))
            {
                var snapshot = await client.CallAsync(scenario == "handoff-count" ? "count_logs" : "search_logs",
                    new { targets, query = "é", enableHandoff = true }, ct);
                RejectIncompleteEnvelope(snapshot);
                RejectFileErrors(snapshot.Result);
                if (!snapshot.Result.GetProperty("isTraversalComplete").GetBoolean() ||
                    snapshot.Result.GetProperty("matchingLineCount").GetInt64() != 0 ||
                    OptionalString(snapshot.Result, "nextCursor") != null)
                    throw new InvalidOperationException("Handoff source was not a complete frozen zero-match snapshot.");
                var handoff = OptionalString(snapshot.Result, "handoffCursor")
                    ?? throw new InvalidOperationException("Handoff source did not retain a boundary.");
                await AppendAsync([0xC3]); // Arrived AFTER the source boundary, BEFORE monitor creation.
                var pending = await PollAsync(new { handoffCursor = handoff }, [], backlog: 1, replay: false);
                if (pending.Result.GetProperty("examinedLineCount").GetInt64() != 0)
                    throw new InvalidOperationException("Handoff evaluated an incomplete character.");
                await AppendAsync([0xA9, 10]);
                await PollAsync(new { cursor }, [new("appended", offset, "é")]);
            }
            else
            {
                var initial = await client.CallAsync("monitor_logs", new
                {
                    targets, query = scenario == "unfinished" ? "^a(?:bc)?$" : (string?)null, useRegex = scenario == "unfinished"
                }, ct);
                cursor = OptionalString(initial.Result, "nextCursor");
                RejectIncompleteEnvelope(initial);
                RejectFileErrors(initial.Result);
                if (cursor == null || initial.Result.GetProperty("selectedFileCount").GetInt32() != files.Length ||
                    initial.Result.GetProperty("files").EnumerateArray().Any(file => file.GetProperty("events").GetArrayLength() != 0))
                    throw new InvalidOperationException("Lifecycle initialization did not establish safe baselines.");
                if (utf8 != null)
                {
                    var character = Convert.FromHexString(utf8[1]);
                    var split = int.Parse(utf8[2], System.Globalization.CultureInfo.InvariantCulture);
                    await AppendAsync(character[..split]);
                    var pending = await PollAsync(new { cursor }, [], backlog: 1);
                    if (pending.Result.GetProperty("examinedLineCount").GetInt64() != 0)
                        throw new InvalidOperationException("A split UTF-8 character was evaluated early.");
                    var unchanged = await PollAsync(new { cursor }, [], backlog: 1);
                    if (unchanged.Result.GetProperty("bytesEvaluated").GetInt64() != 0)
                        throw new InvalidOperationException("An unchanged pending character was rescanned.");
                    await AppendAsync(character[split..]);
                    var text = Encoding.UTF8.GetString(character);
                    await PollAsync(new { cursor }, [new("appended", offset, text)]);
                    await AppendAsync([10]);
                    await PollAsync(new { cursor }, [new("updated", offset, text)]);
                }
                else if (scenario == "utf16-half")
                {
                    await AppendAsync([65]);
                    await PollAsync(new { cursor }, [], backlog: 1);
                    await AppendAsync([0]);
                    await PollAsync(new { cursor }, [new("appended", offset, "A")]);
                }
                else if (scenario == "ansi")
                {
                    await AppendAsync([0xE9, 10]);
                    await PollAsync(new { cursor }, [new("appended", offset, "é")]);
                }
                else if (scenario == "unfinished")
                {
                    await AppendAsync([(byte)'a']);
                    await PollAsync(new { cursor }, [new("appended", 0, "a")]);
                    await AppendAsync([(byte)'b']);
                    await PollAsync(new { cursor }, [new("removed", 0, null)]);
                    await AppendAsync([(byte)'c', 10]);
                    await PollAsync(new { cursor }, [new("appended", 0, "abc")]);
                }
                else if (scenario == "crlf")
                {
                    await AppendAsync(Encoding.UTF8.GetBytes("\nnew"));
                    await PollAsync(new { cursor }, [new("appended", 5, "new")]);
                    await AppendAsync(Encoding.UTF8.GetBytes(" extended\nnext\n"));
                    await PollAsync(new { cursor }, [new("updated", 5, "new extended"), new("appended", 18, "next")]);
                }
                else
                {
                    if (scenario == "replace")
                    {
                        // Hold the old file under another owned name so identity cannot be immediately reused.
                        File.Move(path, backup);
                        await File.WriteAllTextAsync(path, "new\n", ct);
                    }
                    else if (scenario == "missing-reappear") File.Move(path, backup);
                    else if (scenario == "encoding") await AppendAsync([0xFF, 10]);
                    else await File.WriteAllTextAsync(path, "new\n", encoding, ct);
                    await File.AppendAllTextAsync(files[1].Path, "healthy\n", ct);
                    await PollAsync(new { cursor }, [new("appended", 4, "healthy", 1)],
                        error: scenario == "missing-reappear" ? "monitor_file_unavailable" :
                            scenario == "encoding" ? "log_encoding_changed" : "log_generation_changed");
                    if (scenario == "missing-reappear")
                    {
                        File.Move(backup, path); // Restore the SAME generation; do not silently start observation anew.
                        await AppendAsync(Encoding.UTF8.GetBytes("returned\n"));
                        await PollAsync(new { cursor }, [new("appended", offset, "returned")]);
                    }
                }
            }
        }
        finally
        {
            if (cursor != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var released = await client.CallAsync("monitor_logs", new { cursor, release = true }, cleanup.Token);
                releaseMilliseconds = released.Milliseconds;
                if (released.Result.GetProperty("state").GetString() != "released")
                    throw new InvalidOperationException("Lifecycle observation release was not terminal.");
            }
            if (File.Exists(backup)) File.Delete(backup);
        }
        return new("local-lifecycle", "monitor-" + scenario, files.Length, sourceBytes, elapsed, units,
            Hash(Encoding.UTF8.GetBytes(string.Join('\n', identities))), calls, wire, null, null,
            working, privateBytes, progress.ToArray(), bytes, releaseMilliseconds);

        async Task AppendAsync(byte[] content)
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            await stream.WriteAsync(content, ct);
        }

        async Task<McpMeasurementResponse> PollAsync(object arguments, LifecycleEvent[] expected, int backlog = 0,
            string? error = null, bool replay = true)
        {
            var stage = "local-lifecycle/" + scenario + "/poll-" + (calls + 1);
            Stage = stage + "/call";
            var response = await client.CallAsync("monitor_logs", arguments, ct);
            // Update ownership before assertions so failure cleanup releases the committed cursor.
            cursor = OptionalString(response.Result, "nextCursor");
            if (cursor == null) throw new InvalidOperationException("Lifecycle unexpectedly became terminal.");
            var result = response.Result;
            var batch = result.GetProperty("files").EnumerateArray().ToArray();
            var hasPartial = response.Envelope.TryGetProperty("isPartial", out var partial) && partial.GetBoolean();
            Stage = stage + "/progress";
            if (hasPartial != (error != null) ||
                response.Envelope.TryGetProperty("isTruncated", out var truncated) && truncated.GetBoolean() ||
                batch.Length != files.Length || result.GetProperty("checkedFileCount").GetInt32() != files.Length ||
                result.GetProperty("uncheckedFileCount").GetInt32() != 0 || !result.GetProperty("isScopeChecked").GetBoolean() ||
                result.GetProperty("backlogFileCount").GetInt32() != backlog ||
                result.GetProperty("isCaughtUp").GetBoolean() != (backlog == 0 && error == null) ||
                result.GetProperty("isIdle").GetBoolean() || result.GetProperty("bytesEvaluated").GetInt64() > 4L * 1024 * 1024)
                throw new InvalidOperationException("Lifecycle progress, partial state or resource bounds differed.");
            var actual = new List<LifecycleEvent>();
            Stage = stage + "/file-identity";
            for (var i = 0; i < batch.Length; i++)
            {
                var file = batch[i];
                if (file.GetProperty("fileId").GetString() != files[i].Id)
                    throw new InvalidOperationException("Lifecycle file order differed.");
                var code = file.TryGetProperty("error", out var failure) && failure.ValueKind != JsonValueKind.Null
                    ? failure.GetProperty("code").GetString() : null;
                if (code != (i == 0 ? error : null))
                    throw new InvalidOperationException("Lifecycle file error differed.");
                if (error != null && i == 0)
                {
                    if (file.GetProperty("state").GetString() != (error == "monitor_file_unavailable" ? "unavailable" : "stopped"))
                        throw new InvalidOperationException("Lifecycle error continuity state differed.");
                }
                else
                {
                    var generation = OptionalString(file, "generation")
                        ?? throw new InvalidOperationException("Lifecycle generation was unverified.");
                    if (generations.TryGetValue(files[i].Id, out var prior) && prior != generation)
                        throw new InvalidOperationException("Lifecycle line generation changed unexpectedly.");
                    generations[files[i].Id] = generation;
                }
                actual.AddRange(file.GetProperty("events").EnumerateArray().Select(item => new LifecycleEvent(
                    item.GetProperty("kind").GetString()!, item.GetProperty("byteOffset").GetInt64(),
                    OptionalString(item, "text"), i)));
                if (file.GetProperty("events").EnumerateArray().Any(item => item.GetProperty("isTruncated").GetBoolean()))
                    throw new InvalidOperationException("Lifecycle event was truncated.");
            }
            Stage = stage + "/events";
            if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("Lifecycle exact events differed.");
            Stage = stage + "/stopped-count";
            if (result.GetProperty("stoppedFileCount").GetInt32() != (error != null && error != "monitor_file_unavailable" ? 1 : 0))
                throw new InvalidOperationException("Lifecycle stopped count differed.");
            // A cursor poll replays its committed transaction. A handoff without a monitor cursor
            // creates an independent observation each time, so it has no replay envelope.
            Stage = stage + "/replay";
            if (replay)
            {
                var repeated = await client.CallAsync("monitor_logs", arguments, ct);
                if (repeated.Envelope.GetRawText() != response.Envelope.GetRawText())
                    throw new InvalidOperationException("Lifecycle replay differed.");
            }
            calls++;
            elapsed += response.Milliseconds;
            wire += response.WireBytes;
            working = Math.Max(working, response.WorkingSetBytes);
            privateBytes = Math.Max(privateBytes, response.PrivateBytes);
            bytes += result.GetProperty("bytesEvaluated").GetInt64();
            units += actual.Count;
            progress.Add(GetProgress(result));
            identities.Add(JsonSerializer.Serialize(new { events = actual, backlog, error }));
            return response;
        }
    }

    private sealed record LifecycleEvent(string Kind, long Offset, string? Text, int File = 0);
}
