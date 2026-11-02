namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using LogReader.Core;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    private void ValidateExclusion(string? query, bool regex, bool caseSensitive,
        ImmutableArray<ConfiguredLogRequestError>.Builder errors, string parameterName = "excludeQuery")
    {
        if (query == null)
            return;
        if (query.Length == 0 || query.Length > _limits.MaximumQueryCharacters)
            errors.Add(Error(parameterName == "query" ? query.Length == 0 ? "query_required" : "query_too_long" : "invalid_exclude_query",
                $"{parameterName} must be nonempty and within the query character limit."));
        else if (regex && !RegexPatternFactory.TryCreate(query, caseSensitive, out _))
            errors.Add(Error("invalid_regex", $"The {parameterName} regular expression is invalid."));
    }
}
