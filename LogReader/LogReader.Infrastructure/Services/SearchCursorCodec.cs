namespace LogReader.Infrastructure.Services;

using System.Security.Cryptography;
using System.Text.Json;
using LogReader.Core.Models;

internal sealed class SearchCursorCodec
{
    internal const int MaximumCursorLength = 100_000;
    private readonly byte[] _signingKey;

    public SearchCursorCodec()
        : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    internal SearchCursorCodec(byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
            throw new ArgumentException("The cursor signing key must contain at least 32 bytes.", nameof(signingKey));

        _signingKey = signingKey.ToArray();
    }

    public string Encode(SearchCursorPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!IsValid(payload))
            throw new ArgumentException("The search cursor payload is invalid.", nameof(payload));

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HMACSHA256.HashData(_signingKey, payloadBytes);
        var cursor = $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
        if (cursor.Length > MaximumCursorLength)
            throw new ArgumentException("The encoded search cursor exceeds the supported length.", nameof(payload));
        return cursor;
    }

    public bool TryDecode(string? cursor, out SearchCursorPayload? payload)
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

            payload = JsonSerializer.Deserialize<SearchCursorPayload>(payloadBytes);
            return IsValid(payload);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsValid(SearchCursorPayload? payload)
        => payload is
        {
            Version: 2,
            ReferenceDateDayNumber: >= 0 and <= 3_652_058,
            NextStableFileIndex: >= 0,
            CumulativeMatchingLineCount: >= 0,
            CumulativeMatchOccurrenceCount: >= 0,
            CumulativeScannedFileCount: >= 0,
            CumulativeSkippedFileCount: >= 0,
            CumulativeFailedFileCount: >= 0,
            CumulativeMatchedFileCount: >= 0
        } &&
           !string.IsNullOrWhiteSpace(payload.CatalogRevision) &&
           !string.IsNullOrWhiteSpace(payload.RequestFingerprint) &&
           !string.IsNullOrWhiteSpace(payload.TargetFingerprint) &&
           payload.SeenPhysicalPathIdentities is { Length: <= ConfiguredLogLimits.DefaultMaxSearchCandidates } &&
           payload.SeenPhysicalPathIdentities.All(static identity =>
               identity.Length == 32 && identity.All(Uri.IsHexDigit)) &&
           payload.IncompleteReasons is { Length: <= 16 };

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

internal sealed record SearchCursorPayload(
    int Version,
    string CatalogRevision,
    string RequestFingerprint,
    string TargetFingerprint,
    int DateOffsetDays,
    int ReferenceDateDayNumber,
    int NextStableFileIndex,
    string[] SeenPhysicalPathIdentities,
    long CumulativeMatchingLineCount,
    long CumulativeMatchOccurrenceCount,
    int CumulativeScannedFileCount,
    int CumulativeSkippedFileCount,
    int CumulativeFailedFileCount,
    int CumulativeMatchedFileCount,
    bool PriorPagesComplete,
    string[] IncompleteReasons);
