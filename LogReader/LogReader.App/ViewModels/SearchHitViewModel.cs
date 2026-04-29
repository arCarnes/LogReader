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
        PreviewLineText = CreatePreviewLineText(hit.LineText, hit.MatchStart, hit.MatchLength);
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

    private static string CreatePreviewLineText(string lineText, int matchStart, int matchLength)
    {
        if (lineText.Length <= PreviewLineTextLimit)
            return lineText;

        matchStart = Math.Clamp(matchStart, 0, lineText.Length);
        matchLength = Math.Clamp(matchLength, 0, lineText.Length - matchStart);
        var visibleMatchLength = Math.Min(matchLength, PreviewLineTextLimit);
        var contextBefore = Math.Max(0, (PreviewLineTextLimit - visibleMatchLength) / 2);
        var windowStart = Math.Clamp(matchStart - contextBefore, 0, lineText.Length - PreviewLineTextLimit);
        var preview = lineText.Substring(windowStart, PreviewLineTextLimit);

        return $"{(windowStart > 0 ? "..." : string.Empty)}{preview}{(windowStart + PreviewLineTextLimit < lineText.Length ? "..." : string.Empty)}";
    }
}
