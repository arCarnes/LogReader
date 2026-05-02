namespace LogReader.App.ViewModels;

using LogReader.Core.Models;

public class SearchHitViewModel
{
    public long LineNumber { get; }
    public string LineText { get; }
    public int MatchStart { get; }
    public int MatchLength { get; }
    public int? OriginalMatchStart { get; }
    public int? OriginalMatchLength { get; }
    public IReadOnlyList<SearchMatchSpan> Matches { get; }

    public SearchHitViewModel(SearchHit hit)
    {
        LineNumber = hit.LineNumber;
        LineText = hit.LineText;
        MatchStart = hit.MatchStart;
        MatchLength = hit.MatchLength;
        OriginalMatchStart = hit.OriginalMatchStart;
        OriginalMatchLength = hit.OriginalMatchLength;
        Matches = CloneMatches(hit).AsReadOnly();
    }

    internal SearchHit ToModel()
    {
        return new SearchHit
        {
            LineNumber = LineNumber,
            LineText = LineText,
            MatchStart = MatchStart,
            MatchLength = MatchLength,
            OriginalMatchStart = OriginalMatchStart,
            OriginalMatchLength = OriginalMatchLength,
            Matches = Matches.Select(CloneMatch).ToList()
        };
    }

    private static List<SearchMatchSpan> CloneMatches(SearchHit hit)
        => hit.Matches.Count > 0
            ? hit.Matches.Select(CloneMatch).ToList()
            : new List<SearchMatchSpan>
            {
                new()
                {
                    MatchStart = hit.MatchStart,
                    MatchLength = hit.MatchLength,
                    OriginalMatchStart = hit.OriginalMatchStart,
                    OriginalMatchLength = hit.OriginalMatchLength
                }
            };

    private static SearchMatchSpan CloneMatch(SearchMatchSpan match)
        => new()
        {
            MatchStart = match.MatchStart,
            MatchLength = match.MatchLength,
            OriginalMatchStart = match.OriginalMatchStart,
            OriginalMatchLength = match.OriginalMatchLength
        };
}
