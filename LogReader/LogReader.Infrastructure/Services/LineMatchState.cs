namespace LogReader.Infrastructure.Services;

using System.Text.RegularExpressions;

/// <summary>Incremental non-overlapping occurrence enumeration shared with desktop search.</summary>
internal sealed class LineMatchState(string line, string query, bool caseSensitive, Regex? regex)
{
    private int _nextStart;
    private Match? _previous;
    private bool _started;
    private bool _done;

    public int Occurrences { get; private set; }
    public int FirstStart { get; private set; }
    public int FirstLength { get; private set; }
    public string Line { get; } = line;

    public LineMatchState Clone() => new(Line, query, caseSensitive, regex)
    {
        _nextStart = _nextStart, _previous = _previous, _started = _started, _done = _done,
        Occurrences = Occurrences, FirstStart = FirstStart, FirstLength = FirstLength
    };

    public bool TryNext(out (int start, int length) span)
    {
        span = default;
        if (_done)
            return false;
        if (regex != null)
        {
            var match = _started ? _previous!.NextMatch() : regex.Match(Line);
            _started = true;
            _previous = match;
            if (!match.Success)
            {
                _done = true;
                return false;
            }
            span = (match.Index, match.Length);
        }
        else
        {
            if (_nextStart > Line.Length || query.Length == 0)
            {
                _done = true;
                return false;
            }
            var index = Line.IndexOf(query, _nextStart,
                caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                _done = true;
                return false;
            }
            span = (index, query.Length);
            _nextStart = checked(index + query.Length);
        }
        if (Occurrences++ == 0)
        {
            FirstStart = span.start;
            FirstLength = span.length;
        }
        return true;
    }

    public static IEnumerable<(int start, int length)> Enumerate(string line, string query, bool caseSensitive, Regex? regex)
    {
        var state = new LineMatchState(line, query, caseSensitive, regex);
        while (state.TryNext(out var span))
            yield return span;
    }
}
