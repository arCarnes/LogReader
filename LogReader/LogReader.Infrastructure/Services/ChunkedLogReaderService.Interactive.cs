namespace LogReader.Infrastructure.Services;

using System.Text;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class ChunkedLogReaderService
{
    async Task<IInteractiveIndexBuild> IInteractiveLogReaderService.CreateIndexBuildAsync(
        string filePath, FileEncoding encoding, CancellationToken ct)
    {
        var build = new InteractiveIndexBuild(this, filePath, encoding);
        try
        {
            await build.StartAsync(ct).ConfigureAwait(false);
            return build;
        }
        catch
        {
            await build.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class InteractiveIndexBuild : IInteractiveIndexBuild
    {
        private readonly ChunkedLogReaderService _owner;
        private readonly string _path;
        private readonly FileEncoding _encoding;
        private readonly byte[] _buffer = new byte[BufferSize];
        private readonly byte[] _prefix = new byte[64];
        private readonly byte[] _boundary = new byte[64];
        private int _prefixCount;
        private int _boundaryCount;
        private long _boundaryOffset;
        private long _observedLength;
        private DateTime _observedWriteTime;
        private FileStream? _stream;
        private LineIndex? _index;
        private NewlineScanState _newlineState;
        private long _extent;
        private long _position;
        private DateTime _initialWriteTime;
        private int _attempt;
        private bool _complete;
        private bool _disposed;

        public InteractiveIndexBuild(ChunkedLogReaderService owner, string path, FileEncoding encoding)
            => (_owner, _path, _encoding) = (owner, path, encoding);

        public InteractiveIndexProgress Progress => new(_position, _extent, _attempt, _complete);
        public bool CanResume => _index?.GenerationToken.IsKnown == true;

        public async Task StartAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++_attempt > GenerationStabilityAttemptCount)
                throw new IOException("The file changed repeatedly while its line index was being built.");
            await SuspendAsync().ConfigureAwait(false);
            _index?.Dispose();
            _index = null;
            _stream = OpenReadStream(_path, FileOptions.Asynchronous | FileOptions.SequentialScan);
            _extent = GetSnapshotLength(_stream.Length, _encoding);
            _initialWriteTime = _owner.GetLastWriteTimeUtcOrDefault(_stream);
            _observedLength = _stream.Length;
            _observedWriteTime = _initialWriteTime;
            _prefixCount = (int)Math.Min(_prefix.Length, _observedLength);
            if (!await ReadEvidenceAsync(_stream, _prefix.AsMemory(0, _prefixCount), 0, ct).ConfigureAwait(false))
                throw new IOException("The file changed while its index checkpoint was being captured.");
            _boundaryCount = 0;
            var preamble = await ProbePreambleAsync(_stream, _extent, _encoding, ct).ConfigureAwait(false);
            if (preamble.IsPartial)
                _extent = 0;
            _position = preamble.ContentOffset;
            _stream.Position = _position;
            _newlineState = new NewlineScanState();
            _index = new LineIndex
            {
                FilePath = _path,
                GenerationToken = _owner.GetGenerationTokenOrUnknown(_stream),
                LineOffsets = new MappedLineOffsets(int.MaxValue, spillDuringBuild: true)
            };
            _index.LineOffsets.Add(_position);
        }

        private async ValueTask ResumeAsync(CancellationToken ct)
        {
            if (_stream != null)
                return;
            var reopened = OpenReadStream(_path, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var token = _owner.GetGenerationTokenOrUnknown(reopened);
            var writeTime = _owner.GetLastWriteTimeUtcOrDefault(reopened);
            var length = reopened.Length;
            var compatible = false;
            try
            {
                // Unchanged samples cannot prove continuity after a same-size rewrite.
                var sameSizeRewrite = length == _observedLength && _observedWriteTime != default &&
                    writeTime != default && writeTime != _observedWriteTime;
                compatible = !sameSizeRewrite && _index!.GenerationToken.IsKnown && token == _index.GenerationToken && length >= _extent &&
                    (_prefixCount > 0 || length == 0) &&
                    await MatchesEvidenceAsync(reopened, _prefix.AsMemory(0, _prefixCount), 0, ct).ConfigureAwait(false) &&
                    (_observedWriteTime != default && writeTime == _observedWriteTime && length == _observedLength ||
                        await MatchesEvidenceAsync(reopened, _boundary.AsMemory(0, _boundaryCount), _boundaryOffset, ct).ConfigureAwait(false));
            }
            catch
            {
                await reopened.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            if (compatible)
            {
                _stream = reopened;
                _stream.Position = _position;
                _observedLength = length;
                _observedWriteTime = writeTime;
                return;
            }
            await reopened.DisposeAsync().ConfigureAwait(false);
            await StartAsync(ct).ConfigureAwait(false);
        }

        internal static async Task<bool> ReadEvidenceAsync(FileStream stream, Memory<byte> evidence, long offset, CancellationToken ct,
            Action<int>? observeRead = null)
        {
            var copied = 0;
            while (copied < evidence.Length)
            {
                var read = await RandomAccess.ReadAsync(stream.SafeFileHandle, evidence[copied..], offset + copied, ct).ConfigureAwait(false);
                observeRead?.Invoke(read);
                if (read == 0) return false;
                copied += read;
            }
            return true;
        }

        internal static async Task<bool> MatchesEvidenceAsync(FileStream stream, ReadOnlyMemory<byte> expected, long offset, CancellationToken ct,
            Action<int>? observeRead = null)
        {
            if (expected.Length == 0) return true;
            var actual = new byte[expected.Length];
            return await ReadEvidenceAsync(stream, actual, offset, ct, observeRead).ConfigureAwait(false) && actual.AsSpan().SequenceEqual(expected.Span);
        }

        public async ValueTask<bool> AdvanceAsync(int maximumBytes, Func<bool> shouldYield, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (maximumBytes is < 1 or > InteractiveLogLoadLimits.ScanQuantumBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (_complete)
                return true;
            ct.ThrowIfCancellationRequested();
            if (shouldYield())
                return false;
            await ResumeAsync(ct).ConfigureAwait(false);
            var scanned = 0;
            while (_position < _extent && scanned < maximumBytes)
            {
                ct.ThrowIfCancellationRequested();
                if (shouldYield())
                    return false;
                var read = await ReadWithinSnapshotAsync(_stream!, _buffer.AsMemory(0,
                    Math.Min(BufferSize, maximumBytes - scanned)), _extent, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    await StartAsync(ct).ConfigureAwait(false);
                    return false;
                }
                ScanNewlines(_buffer, read, _encoding, _position, _index!.LineOffsets, ref _newlineState);
                _position += read;
                _boundaryCount = Math.Min(read, _boundary.Length);
                _buffer.AsSpan(read - _boundaryCount, _boundaryCount).CopyTo(_boundary);
                _boundaryOffset = _position - _boundaryCount;
                scanned += read;
            }
            if (_position < _extent)
                return false;
            if (_stream!.Length < _extent || !_owner.IsCurrentPathGeneration(_path, _index!.GenerationToken))
            {
                await StartAsync(ct).ConfigureAwait(false);
                return false;
            }
            FlushPendingNewline(_index.LineOffsets, ref _newlineState);
            TrimTrailingEmptyLine(_index.LineOffsets, _position);
            TrimEmptyFileLine(_index.LineOffsets, _position);
            _index.FileSize = _position;
            _index.LastWriteTimeUtc = ResolveStableSnapshotTimestamp(_initialWriteTime,
                _owner.GetLastWriteTimeUtcOrDefault(_stream));
            if (!_index.LineOffsets.IsFrozen)
                _index.LineOffsets.Freeze();
            _complete = true;
            await SuspendAsync().ConfigureAwait(false);
            return true;
        }

        public async ValueTask SuspendAsync()
        {
            if (_stream == null)
                return;
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        public LineIndex TakeCompletedIndex()
        {
            if (!_complete || _index == null)
                throw new InvalidOperationException("The interactive index is not complete or was already taken.");
            var index = _index;
            _index = null;
            return index;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await SuspendAsync().ConfigureAwait(false);
            _index?.Dispose();
            _index = null;
        }
    }

    async Task<RecentLogPreview> IInteractiveLogReaderService.ReadRecentPreviewAsync(
        string filePath, FileEncoding encoding, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, LogReadShare, bufferSize: 1,
            options: FileOptions.Asynchronous | FileOptions.RandomAccess);
        var token = GetGenerationTokenOrUnknown(stream);
        var extent = GetSnapshotLength(stream.Length, encoding);
        var initialWrite = GetLastWriteTimeUtcOrDefault(stream);
        var bytesRead = 0;
        void ObserveRead(int count)
        {
            bytesRead += count;
            _previewReadObserver?.Invoke(count);
        }
        RecentLogPreview Unavailable(string reason)
            => new(Array.Empty<RecentLogPreviewLine>(), token, extent, encoding, bytesRead, reason);
        if (!token.IsKnown)
            return Unavailable("Recent lines preview unavailable: the file identity could not be verified.");
        var prefix = new byte[(int)Math.Min(64, stream.Length)];
        if (!await InteractiveIndexBuild.ReadEvidenceAsync(stream, prefix, 0, ct, ObserveRead).ConfigureAwait(false))
            return Unavailable("Recent lines preview unavailable: the file changed during capture.");
        var preamble = await ProbePreambleAsync(stream, extent, encoding, ct, ObserveRead).ConfigureAwait(false);
        if (preamble.IsPartial || extent <= preamble.ContentOffset)
        {
            using var emptyCurrent = OpenReadStream(filePath, FileOptions.RandomAccess);
            if (GetGenerationTokenOrUnknown(emptyCurrent) != token || GetLastWriteTimeUtcOrDefault(emptyCurrent) != initialWrite ||
                !await InteractiveIndexBuild.MatchesEvidenceAsync(emptyCurrent, prefix, 0, ct, ObserveRead).ConfigureAwait(false))
                return Unavailable("Recent lines preview unavailable: the file changed during capture.");
            return new(Array.Empty<RecentLogPreviewLine>(), token, extent, encoding, bytesRead);
        }
        var width = encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be ? 2 : 1;
        var start = Math.Max(preamble.ContentOffset, extent - InteractiveLogLoadLimits.PreviewBytes);
        if (width == 2 && (start & 1) != 0)
            start++;
        var inspectStart = start > preamble.ContentOffset ? start - width : start;
        var buffer = new byte[checked((int)(extent - inspectStart))];
        stream.Position = inspectStart;
        var copied = 0;
        while (copied < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(copied), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The file ended during preview capture.");
            ObserveRead(read);
            copied += read;
        }
        var boundaryCount = Math.Min(64, buffer.Length);
        var boundary = buffer.AsMemory(buffer.Length - boundaryCount, boundaryCount);
        var boundaryOffset = extent - boundaryCount;
        ct.ThrowIfCancellationRequested();
        using var current = OpenReadStream(filePath, FileOptions.RandomAccess);
        if (GetGenerationTokenOrUnknown(current) != token || current.Length < extent ||
            current.Length == extent && GetLastWriteTimeUtcOrDefault(current) != initialWrite ||
            !await InteractiveIndexBuild.MatchesEvidenceAsync(current, prefix, 0, ct, ObserveRead).ConfigureAwait(false) ||
            !await InteractiveIndexBuild.MatchesEvidenceAsync(current, boundary, boundaryOffset, ct, ObserveRead).ConfigureAwait(false))
            return Unavailable("Recent lines preview unavailable: the file changed during the read.");

        int CodeUnit(int offset) => width == 1 ? buffer[offset] : encoding == FileEncoding.Utf16
            ? buffer[offset] | buffer[offset + 1] << 8 : buffer[offset] << 8 | buffer[offset + 1];
        var first = checked((int)(start - inspectStart));
        var skipLeading = start > preamble.ContentOffset && CodeUnit(0) is not ('\r' or '\n');
        if (start > preamble.ContentOffset && CodeUnit(0) == '\r' && CodeUnit(first) == '\n')
            first += width;
        var spans = new Queue<(int Start, int End)>();
        var lineStart = first;
        for (var offset = first; offset < buffer.Length; offset += width)
        {
            if ((offset & 65535) == 0)
                ct.ThrowIfCancellationRequested();
            var code = CodeUnit(offset);
            if (code is not ('\r' or '\n'))
                continue;
            if (!skipLeading)
                spans.Enqueue((lineStart, offset));
            skipLeading = false;
            if (code == '\r' && offset + width < buffer.Length && CodeUnit(offset + width) == '\n')
                offset += width;
            lineStart = offset + width;
            if (spans.Count > InteractiveLogLoadLimits.PreviewLines)
                spans.Dequeue();
        }
        if (lineStart < buffer.Length && !skipLeading)
        {
            spans.Enqueue((lineStart, buffer.Length));
            if (spans.Count > InteractiveLogLoadLimits.PreviewLines)
                spans.Dequeue();
        }
        if (spans.Count == 0)
            return Unavailable("Recent lines preview unavailable: no complete line fits in the preview window.");
        var decoderEncoding = EncodingHelper.GetEncoding(encoding);
        var lines = new List<RecentLogPreviewLine>();
        var remaining = InteractiveLogLoadLimits.PreviewCharacters;
        foreach (var span in spans.Reverse())
        {
            if (remaining == 0)
                break;
            var limit = Math.Min(remaining, InteractiveLogLoadLimits.PreviewLineCharacters);
            var characters = new char[limit + 1];
            decoderEncoding.GetDecoder().Convert(buffer.AsSpan(span.Start, span.End - span.Start), characters,
                flush: true, out var bytesUsed, out var charsUsed, out var complete);
            var retained = Math.Min(limit, charsUsed);
            if (retained > 0 && char.IsHighSurrogate(characters[retained - 1]))
                retained--;
            var text = new string(characters, 0, retained);
            lines.Add(new RecentLogPreviewLine(inspectStart + span.Start, text,
                !complete || bytesUsed < span.End - span.Start || charsUsed > retained));
            remaining -= retained;
        }
        lines.Reverse();
        using var verified = OpenReadStream(filePath, FileOptions.RandomAccess);
        if (GetGenerationTokenOrUnknown(verified) != token || verified.Length < extent ||
            verified.Length == extent && GetLastWriteTimeUtcOrDefault(verified) != initialWrite ||
            !await InteractiveIndexBuild.MatchesEvidenceAsync(verified, prefix, 0, ct, ObserveRead).ConfigureAwait(false) ||
            !await InteractiveIndexBuild.MatchesEvidenceAsync(verified, boundary, boundaryOffset, ct, ObserveRead).ConfigureAwait(false))
            return Unavailable("Recent lines preview unavailable: the file changed during decoding.");
        return new RecentLogPreview(lines.AsReadOnly(), token, extent, encoding, bytesRead);
    }
}
