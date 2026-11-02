namespace LogReader.Infrastructure.Services;

using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

/// <summary>Applicable pure filter rules; context is retained as surrounding source evidence.</summary>
internal sealed class PreparedLineFilter
{
    private readonly string? _exclude;
    private readonly Regex? _regex;
    private readonly StringComparison _comparison;
    public TimestampRange Range { get; }

    public PreparedLineFilter(SearchRequest request)
    {
        TimestampParser.TryBuildRange(request.FromTimestamp, request.ToTimestamp, out var range, out _);
        Range = range;
        _exclude = request.ExcludeQuery;
        _comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _regex = request.IsRegex && _exclude != null ? RegexPatternFactory.Create(_exclude, request.CaseSensitive) : null;
    }

    public bool Excludes(string line) => _exclude != null &&
        (_regex?.IsMatch(line) ?? line.Contains(_exclude, _comparison));
}
