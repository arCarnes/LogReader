namespace LogReader.Core;

using System.Globalization;
using System.Text.RegularExpressions;

public readonly record struct ParsedTimestamp
{
    private static readonly DateTimeOffset TimeOnlyAnchor = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private ParsedTimestamp(DateTimeOffset value, bool isTimeOnly, string sourceText)
    {
        Value = value;
        IsTimeOnly = isTimeOnly;
        SourceText = sourceText;
    }

    public DateTimeOffset Value { get; }
    public bool IsTimeOnly { get; }
    public string SourceText { get; }
    public TimeSpan TimeOfDay => Value.TimeOfDay;

    public static ParsedTimestamp FromDateTime(DateTimeOffset value, string sourceText)
        => new(value, isTimeOnly: false, sourceText);

    public static ParsedTimestamp FromTimeOfDay(TimeSpan timeOfDay, string sourceText)
        => new(TimeOnlyAnchor.Add(timeOfDay), isTimeOnly: true, sourceText);
}

public readonly record struct TimestampRange(ParsedTimestamp? From, ParsedTimestamp? To, bool CompareUsingTimeOfDay)
{
    public bool HasBounds => From.HasValue || To.HasValue;

    public bool Contains(ParsedTimestamp candidate)
    {
        if (!HasBounds)
            return true;

        if (CompareUsingTimeOfDay)
        {
            var candidateTime = candidate.TimeOfDay;
            if (From.HasValue && candidateTime < From.Value.TimeOfDay)
                return false;
            if (To.HasValue && candidateTime > To.Value.TimeOfDay)
                return false;
            return true;
        }

        if (candidate.IsTimeOnly)
            return false;

        var candidateValue = candidate.Value;
        if (From.HasValue && candidateValue < From.Value.Value)
            return false;
        if (To.HasValue && candidateValue > To.Value.Value)
            return false;

        return true;
    }
}

public static class TimestampParser
{
    private static readonly Regex Iso8601Regex = new(
        @"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DateTimeRegex = new(
        @"\b\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,7})?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeOnlyRegex = new(
        @"\b\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DateTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF"
    };

    private static readonly string[] TimeOnlyFormats =
    {
        "HH:mm:ss",
        "HH:mm:ss.FFFFFFF"
    };

    private const string TimestampFormatError = "Use ISO-8601, yyyy-MM-dd HH:mm:ss, or HH:mm:ss.fff.";

    public static bool TryParseInput(string value, out ParsedTimestamp timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (TryParseIso8601(trimmed, out timestamp))
            return true;

        if (DateTimeOffset.TryParseExact(
                trimmed,
                DateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var dateTimeOffset))
        {
            timestamp = ParsedTimestamp.FromDateTime(dateTimeOffset, trimmed);
            return true;
        }

        if (DateTime.TryParseExact(
                trimmed,
                TimeOnlyFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timeOnly))
        {
            timestamp = ParsedTimestamp.FromTimeOfDay(timeOnly.TimeOfDay, trimmed);
            return true;
        }

        return false;
    }

    public static bool TryParseFromLogLine(string line, out ParsedTimestamp timestamp)
    {
        timestamp = default;
        if (string.IsNullOrEmpty(line))
            return false;

        var isoMatch = Iso8601Regex.Match(line);
        if (isoMatch.Success && TryParseInput(isoMatch.Value, out timestamp))
            return true;

        var dateTimeMatch = DateTimeRegex.Match(line);
        if (dateTimeMatch.Success && TryParseInput(dateTimeMatch.Value, out timestamp))
            return true;

        var timeOnlyMatch = TimeOnlyRegex.Match(line);
        if (timeOnlyMatch.Success && TryParseInput(timeOnlyMatch.Value, out timestamp))
            return true;

        return false;
    }

    public static bool TryBuildRange(string? fromValue, string? toValue, out TimestampRange range, out string? error)
    {
        range = default;
        error = null;

        ParsedTimestamp? from = null;
        ParsedTimestamp? to = null;

        if (!string.IsNullOrWhiteSpace(fromValue))
        {
            if (!TryParseInput(fromValue, out var parsedFrom))
            {
                error = $"Invalid 'From' timestamp. {TimestampFormatError}";
                return false;
            }

            from = parsedFrom;
        }

        if (!string.IsNullOrWhiteSpace(toValue))
        {
            if (!TryParseInput(toValue, out var parsedTo))
            {
                error = $"Invalid 'To' timestamp. {TimestampFormatError}";
                return false;
            }

            to = parsedTo;
        }

        if (from.HasValue && to.HasValue)
        {
            if (from.Value.IsTimeOnly != to.Value.IsTimeOnly)
            {
                error = "'From' and 'To' must both include dates or both be time-only.";
                return false;
            }

            var bothTimeOnly = from.Value.IsTimeOnly;
            if (bothTimeOnly)
            {
                if (from.Value.TimeOfDay > to.Value.TimeOfDay)
                {
                    error = "'From' time must be earlier than or equal to 'To' time.";
                    return false;
                }
            }
            else if (from.Value.Value > to.Value.Value)
            {
                error = "'From' timestamp must be earlier than or equal to 'To' timestamp.";
                return false;
            }
        }

        var compareUsingTimeOfDay = (from?.IsTimeOnly ?? false) || (to?.IsTimeOnly ?? false);
        range = new TimestampRange(from, to, compareUsingTimeOfDay);
        return true;
    }

    private static bool TryParseIso8601(string value, out ParsedTimestamp timestamp)
    {
        timestamp = default;
        var match = Iso8601Regex.Match(value);
        if (!match.Success || match.Index != 0 || match.Length != value.Length)
            return false;

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var dateTimeOffset))
        {
            return false;
        }

        timestamp = ParsedTimestamp.FromDateTime(dateTimeOffset, value);
        return true;
    }
}
