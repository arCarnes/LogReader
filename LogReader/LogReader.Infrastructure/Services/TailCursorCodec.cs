namespace LogReader.Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogReader.Core.Models;

internal sealed class TailCursorCodec
{
    private const int MaximumCursorLength = 4_096;
    private readonly byte[] _signingKey;

    public TailCursorCodec()
        : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    internal TailCursorCodec(byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
            throw new ArgumentException("The cursor signing key must contain at least 32 bytes.", nameof(signingKey));

        _signingKey = signingKey.ToArray();
    }

    public string GetPathIdentity(string normalizedPath)
        => Protect($"path:{normalizedPath.ToUpperInvariant()}");

    public string GetGenerationIdentity(LineIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var token = index.GenerationToken;
        return token.IsKnown
            ? Protect($"generation:{token.VolumeId:X16}:{token.FileId:X16}")
            : Protect("generation:unknown");
    }

    public string GetGenerationIdentity(FileScanGenerationEvidence evidence)
    {
        var token = evidence.Token;
        var identity = token.IsKnown
            ? $"{token.VolumeId:X16}:{token.FileId:X16}"
            : "unknown";
        return $"{evidence.Correlation.ToString().ToLowerInvariant()}:{Protect($"generation:{identity}")}";
    }

    public string Encode(TailCursorPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HMACSHA256.HashData(_signingKey, payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    public bool TryDecode(string? cursor, out TailCursorPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaximumCursorLength)
            return false;

        var separator = cursor.IndexOf('.');
        if (separator <= 0 || separator != cursor.LastIndexOf('.'))
            return false;

        try
        {
            var payloadBytes = Base64UrlDecode(cursor[..separator]);
            var suppliedSignature = Base64UrlDecode(cursor[(separator + 1)..]);
            var expectedSignature = HMACSHA256.HashData(_signingKey, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
                return false;

            payload = JsonSerializer.Deserialize<TailCursorPayload>(payloadBytes);
            return payload is { Version: 1 } &&
                   !string.IsNullOrWhiteSpace(payload.FileId) &&
                   !string.IsNullOrWhiteSpace(payload.PathIdentity) &&
                   !string.IsNullOrWhiteSpace(payload.GenerationIdentity) &&
                   payload.LastLineNumber >= 0 &&
                   payload.LastOffset >= 0 &&
                   payload.FileSize >= 0 &&
                   Enum.IsDefined(payload.Encoding);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or OverflowException)
        {
            return false;
        }
    }

    private string Protect(string value)
        => Base64UrlEncode(HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(value)))[..22];

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        var decoded = Convert.FromBase64String(padded);
        if (!StringComparer.Ordinal.Equals(Base64UrlEncode(decoded), value))
            throw new FormatException("The base64url value is not canonical.");

        return decoded;
    }
}

internal sealed record TailCursorPayload(
    int Version,
    string FileId,
    string PathIdentity,
    FileEncoding Encoding,
    string GenerationIdentity,
    int LastLineNumber,
    long LastOffset,
    long FileSize);
