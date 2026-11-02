namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using LogReader.Core;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    private void ValidateExclusion(string? query, bool regex, bool caseSensitive,
        ImmutableArray<ConfiguredLogRequestError>.Builder errors)
    {
        if (query == null)
            return;
        if (query.Length == 0 || query.Length > _limits.MaximumQueryCharacters)
            errors.Add(Error("invalid_exclude_query", "excludeQuery must be nonempty and within the query character limit."));
        else if (regex && !RegexPatternFactory.TryCreate(query, caseSensitive, out _))
            errors.Add(Error("invalid_regex", "The exclusion regular expression is invalid."));
    }
}
