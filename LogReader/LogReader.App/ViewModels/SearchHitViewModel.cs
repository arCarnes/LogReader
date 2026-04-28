namespace LogReader.App.ViewModels;

using LogReader.Core.Models;

public class SearchHitViewModel
{
    private const int PreviewLineTextLimit = 2000;

    public long LineNumber { get; }
    public string LineText { get; }
    public string PreviewLineText { get; }
    public int MatchStart { get; }
    public int MatchLength { get; }
    public int? OriginalMatchStart { get; }
    public int? OriginalMatchLength { get; }

    public SearchHitViewModel(SearchHit hit)
    {
        LineNumber = hit.LineNumber;
        LineText = hit.LineText;
        PreviewLineText = CreatePreviewLineText(hit.LineText);
        MatchStart = hit.MatchStart;
        MatchLength = hit.MatchLength;
        OriginalMatchStart = hit.OriginalMatchStart;
        OriginalMatchLength = hit.OriginalMatchLength;
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
            OriginalMatchLength = OriginalMatchLength
        };
    }

    private static string CreatePreviewLineText(string lineText)
    {
        return lineText.Length > PreviewLineTextLimit
            ? lineText[..PreviewLineTextLimit] + "..."
            : lineText;
    }
}
