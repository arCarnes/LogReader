namespace LogReader.App.ViewModels;

using LogReader.Core.Models;

public class SearchHitViewModel
{
    public long LineNumber { get; }
    public string LineText { get; }
    public int MatchStart { get; }
    public int MatchLength { get; }

    public SearchHitViewModel(SearchHit hit)
    {
        LineNumber = hit.LineNumber;
        LineText = hit.LineText;
        MatchStart = hit.MatchStart;
        MatchLength = hit.MatchLength;
    }

    internal SearchHit ToModel()
    {
        return new SearchHit
        {
            LineNumber = LineNumber,
            LineText = LineText,
            MatchStart = MatchStart,
            MatchLength = MatchLength
        };
    }
}
