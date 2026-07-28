namespace LogReader.Infrastructure.Services;

internal sealed class SnapshotReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;

    public SnapshotReadStream(Stream inner, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
            throw new ArgumentException("The stream must be readable.", nameof(inner));
        if (!inner.CanSeek)
            throw new ArgumentException("The stream must be seekable.", nameof(inner));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        _inner = inner;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _inner.Position;
        set
        {
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value));

            _inner.Position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, LimitReadCount(count));

    public override int Read(Span<byte> buffer)
        => _inner.Read(buffer[..LimitReadCount(buffer.Length)]);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, LimitReadCount(count), cancellationToken);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer[..LimitReadCount(buffer.Length)], cancellationToken);

    public override int ReadByte()
        => Position < _length ? _inner.ReadByte() : -1;

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    private int LimitReadCount(int requestedCount)
    {
        if (requestedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedCount));

        var remaining = _length - Position;
        return remaining <= 0
            ? 0
            : (int)Math.Min(requestedCount, remaining);
    }
}
