namespace LogReader.Infrastructure.Services;

using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogReader.Core.Models;

public static class LiveLogIpcFraming
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static async Task<LiveLogIpcFrame?> ReadFrameAsync(
        Stream stream,
        int maximumFrameBytes = LiveLogIpcProtocol.MaximumFrameBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateMaximum(maximumFrameBytes);
        var lengthBuffer = new byte[sizeof(int)];
        var prefixBytes = await ReadExactAsync(stream, lengthBuffer, allowCleanEof: true, ct).ConfigureAwait(false);
        if (prefixBytes == 0)
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length is < 1 || length > maximumFrameBytes)
            throw new LiveLogIpcProtocolException("invalid_frame_size");

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactAsync(stream, rented.AsMemory(0, length), allowCleanEof: false, ct).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<LiveLogIpcFrame>(
                           rented.AsSpan(0, length),
                           SerializerOptions)
                       ?? throw new LiveLogIpcProtocolException("invalid_frame");
            }
            catch (JsonException)
            {
                throw new LiveLogIpcProtocolException("invalid_json");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        LiveLogIpcFrame frame,
        int maximumFrameBytes = LiveLogIpcProtocol.MaximumFrameBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        ValidateMaximum(maximumFrameBytes);
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SerializerOptions);
        if (payload.Length > maximumFrameBytes)
            throw new LiveLogIpcProtocolException("frame_too_large");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        bool allowCleanEof,
        CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowCleanEof && total == 0)
                    return 0;
                throw new LiveLogIpcProtocolException("partial_frame");
            }

            total += read;
        }

        return total;
    }

    private static void ValidateMaximum(int maximumFrameBytes)
    {
        if (maximumFrameBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
    }
}

public sealed class LiveLogIpcProtocolException : IOException
{
    public LiveLogIpcProtocolException(string code)
        : base("The live log IPC frame is invalid.")
    {
        Code = code;
    }

    public string Code { get; }
}
