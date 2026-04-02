namespace LogReader.App.ViewModels;

public abstract class SearchResultsRowViewModel
{
    protected SearchResultsRowViewModel(FileSearchResultViewModel fileResult)
    {
        FileResult = fileResult;
    }

    public FileSearchResultViewModel FileResult { get; }
}

public sealed class SearchResultFileHeaderRowViewModel : SearchResultsRowViewModel
{
    internal SearchResultFileHeaderRowViewModel(FileSearchResultViewModel fileResult)
        : base(fileResult)
    {
    }
}

public sealed class SearchResultHitRowViewModel : SearchResultsRowViewModel
{
    internal SearchResultHitRowViewModel(FileSearchResultViewModel fileResult, int hitIndex, SearchHitViewModel hit)
        : base(fileResult)
    {
        HitIndex = hitIndex;
        Hit = hit;
    }

    public int HitIndex { get; }

    public SearchHitViewModel Hit { get; }
}
