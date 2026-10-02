namespace LogReader.Infrastructure.Services;

using System.Diagnostics;
using System.Buffers;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class ScanWorkBudget(int milliseconds, long maximumBytes, Func<long>? timestamp = null)
{
    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    private readonly long _start = (timestamp ?? Stopwatch.GetTimestamp)();
    private long _bytes;

    public long Bytes => Interlocked.Read(ref _bytes);
    public bool IsStopped => Bytes >= maximumBytes ||
        Stopwatch.GetElapsedTime(_start, _timestamp()).TotalMilliseconds >= milliseconds;
    public string StopReason => Bytes >= maximumBytes ? "scan_budget" : "time_slice";
    public long RemainingBytes => Math.Max(0, maximumBytes - Bytes);
    public void AddBytes(int count) => Interlocked.Add(ref _bytes, count);
    public bool TryAddBytes(int count)
    {
        while (true)
        {
            var before = Bytes;
            if (before >= maximumBytes || count > maximumBytes - before && count > 2)
                return false;
            if (Interlocked.CompareExchange(ref _bytes, before + count, before) == before)
                return true;
        }
    }
}

internal sealed class ResumableFileCheckpoint
{
    public bool Initialized;
    public FileEncoding Encoding;
    public FileGenerationToken Generation;
    public long Extent;
    public long InitialLength;
    public long ObservedLength;
    public DateTime LastWriteUtc;
    public DateTime ObservedWriteUtc;
    public long ReadOffset;
    public long LineNumber = 1;
    public byte[] Partial = [];
    public int PartialCount;
    public bool PendingCr;
    public LineMatchState? Matcher;
    public ParsedTimestamp? Timestamp;
    public int LineBytes;
    public bool Done;
    public HashSet<string> Reasons = new(StringComparer.Ordinal);

    public ResumableFileCheckpoint Clone() => new()
    {
        Initialized = Initialized, Encoding = Encoding, Generation = Generation, Extent = Extent,
        InitialLength = InitialLength, ObservedLength = ObservedLength, ObservedWriteUtc = ObservedWriteUtc,
        LastWriteUtc = LastWriteUtc, ReadOffset = ReadOffset, LineNumber = LineNumber,
        Partial = Partial.ToArray(), PartialCount = PartialCount, PendingCr = PendingCr,
        Matcher = Matcher?.Clone(), Timestamp = Timestamp, LineBytes = LineBytes, Done = Done,
        Reasons = new HashSet<string>(Reasons, StringComparer.Ordinal)
    };

    public long RetainedBytes => 256L + Partial.LongLength + (Matcher?.Line.Length ?? 0) * 2L;
    public void ReleaseBuffers() { Partial = []; PartialCount = 0; Matcher = null; }
}

internal sealed record EvaluatedScanLine(long Number, string Text, SearchHit? Hit, int Occurrences,
    ParsedTimestamp? Timestamp, int Bytes);

internal sealed class ResumableScanReader(FileStream stream) : IAsyncDisposable
{
    public FileStream Stream { get; } = stream;
    public byte[] Buffer { get; } = ArrayPool<byte>.Shared.Rent(256 * 1024);
    public int Position;
    public int Count;
    public async ValueTask DisposeAsync()
    {
        try { await Stream.DisposeAsync().ConfigureAwait(false); }
        finally { ArrayPool<byte>.Shared.Return(Buffer, clearArray: true); }
    }
}

/// <summary>Reads an explicit frozen extent and retains logical, rather than buffered-stream, positions.</summary>
internal sealed class ResumableLogScanner
{
    private const int BufferBytes = 256 * 1024;
    private readonly IEncodingDetectionService _encodingDetection;
    private readonly Func<FileStream, FileGenerationToken> _generation;

    public ResumableLogScanner(IEncodingDetectionService encodingDetection,
        Func<FileStream, FileGenerationToken>? generation = null)
    {
        _encodingDetection = encodingDetection;
        _generation = generation ?? FileGenerationTokenProvider.Capture;
    }

    public async Task<ResumableScanReader> OpenAsync(string path, ResumableFileCheckpoint state, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferBytes, FileOptions.SequentialScan | FileOptions.Asynchronous);
        try
        {
            var generation = _generation(stream);
            var length = stream.Length;
            var write = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            if (!state.Initialized)
            {
                state.Encoding = _encodingDetection.ResolveEncodingDecision(path, FileEncoding.Auto).ResolvedEncoding;
                state.Generation = generation;
                state.Extent = state.Encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be ? length & ~1L : length;
                state.LastWriteUtc = write;
                state.InitialLength = length;
                state.ObservedLength = length;
                state.ObservedWriteUtc = write;
                var expected = state.Encoding switch
                {
                    FileEncoding.Utf8 or FileEncoding.Utf8Bom => new byte[] { 0xEF, 0xBB, 0xBF },
                    FileEncoding.Utf16 => new byte[] { 0xFF, 0xFE },
                    FileEncoding.Utf16Be => new byte[] { 0xFE, 0xFF },
                    _ => Array.Empty<byte>()
                };
                var probe = new byte[expected.Length];
                var read = await stream.ReadAsync(probe.AsMemory(0, (int)Math.Min(probe.Length, state.Extent)), ct).ConfigureAwait(false);
                if (expected.Length > 0 && read == expected.Length && probe.AsSpan().SequenceEqual(expected))
                    state.ReadOffset = expected.Length;
                state.Initialized = true;
            }
            else if (!state.Generation.IsKnown || !generation.IsKnown)
                throw new ScanFileException("log_generation_unverified");
            else if (generation != state.Generation || length < state.ObservedLength ||
                     length == state.ObservedLength && write != state.ObservedWriteUtc)
                throw new ScanFileException("log_generation_changed");
            if (length != state.InitialLength || write != state.LastWriteUtc)
                state.Reasons.Add("file_changed_during_search");
            state.ObservedLength = length;
            state.ObservedWriteUtc = write;
            stream.Position = state.ReadOffset;
            return new ResumableScanReader(stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void ValidateAfterRead(FileStream stream, ResumableFileCheckpoint state)
    {
        var length = stream.Length;
        var write = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        if (length < state.ObservedLength || length == state.ObservedLength && write != state.ObservedWriteUtc)
            throw new ScanFileException("log_generation_changed");
        if (length != state.InitialLength || write != state.LastWriteUtc)
            state.Reasons.Add("file_changed_during_search");
        state.ObservedLength = length;
        state.ObservedWriteUtc = write;
        if (!state.Generation.IsKnown)
        {
            state.Reasons.Add("file_generation_unverified");
        }
    }

    public async Task<EvaluatedScanLine?> ReadNextAsync(ResumableScanReader reader, ResumableFileCheckpoint state,
        SearchRequest request, Regex? regex, ScanWorkBudget budget, int maximumLineBytes, CancellationToken ct)
    {
        if (state.Done || budget.IsStopped)
            return null;
        if (state.Matcher == null)
        {
            var buffer = reader.Buffer;
            var stream = reader.Stream;
            var unit = state.Encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be ? 2 : 1;
            var complete = false;
            while (!complete && !budget.IsStopped)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = state.Extent - state.ReadOffset;
                if (remaining == 0)
                {
                    if (state.PartialCount == 0 && !state.PendingCr)
                    {
                        state.Done = true;
                        return null;
                    }
                    complete = true;
                    break;
                }
                if (reader.Position == reader.Count)
                {
                    var allowance = Math.Min(buffer.Length, Math.Min(remaining, budget.RemainingBytes));
                    // A UTF-16 code unit is indivisible even with an injected odd byte budget.
                    var count = (int)Math.Max(unit, allowance - allowance % unit);
                    stream.Position = state.ReadOffset;
                    var read = await stream.ReadAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    if (read == 0)
                        throw new ScanFileException("log_generation_changed");
                    if (unit == 2 && read % 2 != 0)
                    {
                        var extra = await stream.ReadAsync(buffer.AsMemory(read, 1), ct).ConfigureAwait(false);
                        if (extra != 1)
                            throw new ScanFileException("log_generation_changed");
                        read++;
                    }
                    reader.Position = 0;
                    reader.Count = read;
                }
                while (reader.Position < reader.Count && !budget.IsStopped)
                {
                    var offset = reader.Position;
                    var value = unit == 1 ? buffer[offset] : state.Encoding == FileEncoding.Utf16
                        ? buffer[offset] | buffer[offset + 1] << 8
                        : buffer[offset] << 8 | buffer[offset + 1];
                    if (state.PendingCr)
                    {
                        if (value == '\n')
                        {
                            if (!budget.TryAddBytes(unit))
                                return null;
                            state.ReadOffset += unit;
                            state.LineBytes += unit;
                            reader.Position += unit;
                        }
                        complete = true;
                        break;
                    }
                    var available = (int)Math.Min(reader.Count - reader.Position,
                        Math.Max(unit, budget.RemainingBytes - budget.RemainingBytes % unit));
                    var contentBytes = available;
                    var ending = -1;
                    if (unit == 1)
                    {
                        var index = buffer.AsSpan(offset, available).IndexOfAny((byte)'\r', (byte)'\n');
                        if (index >= 0)
                        {
                            contentBytes = index;
                            ending = buffer[offset + index];
                        }
                    }
                    else
                    {
                        for (var index = 0; index < available; index += unit)
                        {
                            var character = state.Encoding == FileEncoding.Utf16
                                ? buffer[offset + index] | buffer[offset + index + 1] << 8
                                : buffer[offset + index] << 8 | buffer[offset + index + 1];
                            if (character is '\r' or '\n')
                            {
                                contentBytes = index;
                                ending = character;
                                break;
                            }
                        }
                    }
                    var consumed = contentBytes + (ending < 0 ? 0 : unit);
                    if (state.LineBytes + consumed > maximumLineBytes)
                        throw new ScanFileException("log_line_too_large");
                    if (!budget.TryAddBytes(consumed))
                        return null;
                    if (contentBytes > 0)
                    {
                        var needed = state.PartialCount + contentBytes;
                        if (needed > state.Partial.Length)
                            Array.Resize(ref state.Partial, Math.Min(maximumLineBytes,
                                Math.Max(needed, Math.Max(4096, state.Partial.Length * 2))));
                        buffer.AsSpan(offset, contentBytes).CopyTo(state.Partial.AsSpan(state.PartialCount));
                        state.PartialCount += contentBytes;
                    }
                    state.ReadOffset += consumed;
                    state.LineBytes += consumed;
                    reader.Position += consumed;
                    if (ending == '\r')
                    {
                        state.PendingCr = true;
                        continue;
                    }
                    if (ending == '\n')
                    {
                        complete = true;
                        break;
                    }
                }
                if (state.LineBytes > maximumLineBytes)
                    throw new ScanFileException("log_line_too_large");
            }
            if (!complete)
                return null;
            var line = EncodingHelper.GetEncoding(state.Encoding).GetString(state.Partial, 0, state.PartialCount);
            state.PartialCount = 0;
            state.PendingCr = false;
            state.Timestamp = null;
            TimestampParser.TryBuildRange(request.FromTimestamp, request.ToTimestamp, out var range, out _);
            if (range.HasBounds || request.TimestampAggregation != null)
            {
                if (!TimestampParser.TryParseFromLogLine(line, out var parsed) || !range.Contains(parsed))
                    return FinishLine(state, line, null, 0);
                state.Timestamp = parsed;
            }
            state.Matcher = new LineMatchState(line, request.Query, request.CaseSensitive, regex);
        }
        while (!budget.IsStopped)
        {
            ct.ThrowIfCancellationRequested();
            if (!state.Matcher.TryNext(out _))
            {
                var matcher = state.Matcher;
                var hit = matcher.Occurrences == 0 || request.MaxHitsPerFile == 0 ? null : SearchService.RetainResumableHit(matcher.Line,
                    state.LineNumber, matcher.FirstStart, matcher.FirstLength,
                    request.MaxRetainedLineTextLength ?? 4096);
                return FinishLine(state, matcher.Line, hit, matcher.Occurrences);
            }
        }
        return null;
    }

    private static EvaluatedScanLine FinishLine(ResumableFileCheckpoint state, string line, SearchHit? hit, int occurrences)
    {
        var evaluated = new EvaluatedScanLine(state.LineNumber++, line, hit, occurrences, state.Timestamp, state.LineBytes);
        state.Matcher = null;
        state.LineBytes = 0;
        if (state.ReadOffset == state.Extent)
            state.Done = true;
        return evaluated;
    }
}

internal sealed class ScanFileException(string code) : IOException(code)
{
    public string Code { get; } = code;
}
