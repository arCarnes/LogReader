namespace LogReader.Core;

using System.Text;
using LogReader.Core.Models;

public static class EncodingHelper
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);
    private static readonly Encoding Ansi;

    static EncodingHelper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Ansi = Encoding.GetEncoding(1252);
    }

    public static Encoding GetEncoding(FileEncoding encoding) => encoding switch
    {
        FileEncoding.Auto => Utf8NoBom,
        FileEncoding.Utf8 => Utf8NoBom,
        FileEncoding.Utf8Bom => Utf8WithBom,
        FileEncoding.Ansi => Ansi,
        FileEncoding.Utf16 => Encoding.Unicode,
        FileEncoding.Utf16Be => Encoding.BigEndianUnicode,
        _ => Utf8NoBom
    };

    public static FileEncoding DetectFileEncoding(ReadOnlySpan<byte> sample, FileEncoding fallback = FileEncoding.Utf8)
        => DetectFileEncodingWithReason(sample, fallback).encoding;

    public static EncodingDecision ResolveManualEncodingDecision(FileEncoding selectedEncoding)
    {
        var resolvedManual = selectedEncoding == FileEncoding.Utf8Bom ? FileEncoding.Utf8Bom : selectedEncoding;
        return new EncodingDecision(selectedEncoding, resolvedManual, $"Manual -> {GetEncodingDisplayName(resolvedManual)}");
    }

    public static EncodingDecision ResolveAutoEncodingDecision(ReadOnlySpan<byte> sample, FileEncoding fallback = FileEncoding.Utf8)
    {
        var (encoding, reason) = DetectFileEncodingWithReason(sample, fallback);
        return new EncodingDecision(
            FileEncoding.Auto,
            encoding,
            $"Auto -> {GetEncodingDisplayName(encoding)} ({reason})");
    }

    public static string GetEncodingDisplayName(FileEncoding encoding) => encoding switch
    {
        FileEncoding.Auto => "Auto (Detect)",
        FileEncoding.Utf8 => "UTF-8",
        FileEncoding.Utf8Bom => "UTF-8 (BOM)",
        FileEncoding.Utf16 => "UTF-16",
        FileEncoding.Utf16Be => "UTF-16 BE",
        FileEncoding.Ansi => "ANSI",
        _ => "UTF-8"
    };

    private static (FileEncoding encoding, string reason) DetectFileEncodingWithReason(ReadOnlySpan<byte> sample, FileEncoding fallback)
    {
        var normalizedFallback = NormalizeFallbackEncoding(fallback);

        if (sample.Length >= 3 &&
            sample[0] == 0xEF &&
            sample[1] == 0xBB &&
            sample[2] == 0xBF)
        {
            return (FileEncoding.Utf8Bom, "BOM signature");
        }

        if (sample.Length >= 2)
        {
            if (sample[0] == 0xFF && sample[1] == 0xFE)
                return (FileEncoding.Utf16, "BOM signature");
            if (sample[0] == 0xFE && sample[1] == 0xFF)
                return (FileEncoding.Utf16Be, "BOM signature");
        }

        if (TryDetectUtf16WithoutBom(sample, out var utf16Encoding))
            return (utf16Encoding, "UTF-16 byte pattern");

        if (IsValidUtf8(sample, out var hasMultibyteUtf8Chars) && hasMultibyteUtf8Chars)
            return (FileEncoding.Utf8, "valid UTF-8 multibyte sequence");

        return (normalizedFallback, $"fallback to {GetEncodingDisplayName(normalizedFallback)}");
    }

    private static bool TryDetectUtf16WithoutBom(ReadOnlySpan<byte> sample, out FileEncoding encoding)
    {
        encoding = FileEncoding.Utf8;
        var pairs = sample.Length / 2;
        if (pairs < 4)
            return false;

        var evenZeroCount = 0;
        var oddZeroCount = 0;
        for (var i = 0; i < pairs * 2; i += 2)
        {
            if (sample[i] == 0x00)
                evenZeroCount++;
            if (sample[i + 1] == 0x00)
                oddZeroCount++;
        }

        var evenZeroHigh = evenZeroCount >= (pairs * 3) / 5;
        var oddZeroHigh = oddZeroCount >= (pairs * 3) / 5;
        var evenZeroLow = evenZeroCount <= pairs / 5;
        var oddZeroLow = oddZeroCount <= pairs / 5;

        if (oddZeroHigh && evenZeroLow)
        {
            encoding = FileEncoding.Utf16;
            return true;
        }

        if (evenZeroHigh && oddZeroLow)
        {
            encoding = FileEncoding.Utf16Be;
            return true;
        }

        return false;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> data, out bool hasMultibyteChars)
    {
        hasMultibyteChars = false;

        var i = 0;
        while (i < data.Length)
        {
            var b0 = data[i];
            if (b0 <= 0x7F)
            {
                i++;
                continue;
            }

            hasMultibyteChars = true;

            if (b0 is >= 0xC2 and <= 0xDF)
            {
                if (i + 1 >= data.Length)
                    return false;
                if (!IsContinuationByte(data[i + 1]))
                    return false;
                i += 2;
                continue;
            }

            if (b0 is >= 0xE0 and <= 0xEF)
            {
                if (i + 2 >= data.Length)
                    return false;

                var b1 = data[i + 1];
                var b2 = data[i + 2];
                if (!IsContinuationByte(b2))
                    return false;
                if (b0 == 0xE0 && b1 is < 0xA0 or > 0xBF)
                    return false;
                if (b0 == 0xED && b1 is < 0x80 or > 0x9F)
                    return false;
                if (b0 is not (0xE0 or 0xED) && !IsContinuationByte(b1))
                    return false;

                i += 3;
                continue;
            }

            if (b0 is >= 0xF0 and <= 0xF4)
            {
                if (i + 3 >= data.Length)
                    return false;

                var b1 = data[i + 1];
                var b2 = data[i + 2];
                var b3 = data[i + 3];
                if (!IsContinuationByte(b2) || !IsContinuationByte(b3))
                    return false;
                if (b0 == 0xF0 && b1 is < 0x90 or > 0xBF)
                    return false;
                if (b0 == 0xF4 && b1 is < 0x80 or > 0x8F)
                    return false;
                if (b0 is not (0xF0 or 0xF4) && !IsContinuationByte(b1))
                    return false;

                i += 4;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsContinuationByte(byte value) => value is >= 0x80 and <= 0xBF;

    private static FileEncoding NormalizeFallbackEncoding(FileEncoding fallback)
        => fallback == FileEncoding.Auto ? FileEncoding.Utf8 : fallback;

    public readonly record struct EncodingDecision(
        FileEncoding SelectedEncoding,
        FileEncoding ResolvedEncoding,
        string StatusText);
}
