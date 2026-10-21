namespace LogReader.App.Services;

internal interface ISeasonalIconDateRule
{
    bool IsActive(DateOnly date);
}

internal sealed class HolidayWeekRule : ISeasonalIconDateRule
{
    private readonly int _month;
    private readonly int _day;

    internal HolidayWeekRule(int month, int day)
    {
        _ = new DateOnly(2000, month, day);
        _month = month;
        _day = day;
    }

    public bool IsActive(DateOnly date)
    {
        // A holiday's week can extend into the adjacent calendar year.
        for (var year = Math.Max(1, date.Year - 1); year <= Math.Min(9999, date.Year + 1); year++)
        {
            if (_day > DateTime.DaysInMonth(year, _month))
                continue;

            var holiday = new DateOnly(year, _month, _day);
            var monday = holiday.DayNumber - ((int)holiday.DayOfWeek + 6) % 7;
            if (date.DayNumber >= monday && date.DayNumber < monday + 7)
                return true;
        }

        return false;
    }
}

internal sealed class AnnualDateRangeRule : ISeasonalIconDateRule
{
    private readonly int _start;
    private readonly int _end;

    internal AnnualDateRangeRule(int startMonth, int startDay, int endMonth, int endDay)
    {
        _ = new DateOnly(2000, startMonth, startDay);
        _ = new DateOnly(2000, endMonth, endDay);
        _start = startMonth * 100 + startDay;
        _end = endMonth * 100 + endDay;
    }

    public bool IsActive(DateOnly date)
    {
        var value = date.Month * 100 + date.Day;
        return _start <= _end
            ? value >= _start && value <= _end
            : value >= _start || value <= _end;
    }
}

internal sealed record SeasonalIconDefinition(
    string Id, string IconResource, ISeasonalIconDateRule DateRule, int Priority = 0);

internal static class SeasonalIcons
{
    internal const string ResourceKey = "AppWindowIconResource";
    internal const string StandardIcon = "pack://application:,,,/WeezTail;component/Assets/favicon.ico";
    internal const string HalloweenIcon = "pack://application:,,,/WeezTail;component/Assets/Seasonal/halloween.ico";

    internal static IReadOnlyList<SeasonalIconDefinition> Catalog { get; } = Array.AsReadOnly(
        new[] { new SeasonalIconDefinition("halloween", HalloweenIcon, new HolidayWeekRule(10, 31)) });

    internal static SeasonalIconDefinition? Resolve(DateOnly date, IReadOnlyList<SeasonalIconDefinition>? catalog = null)
    {
        SeasonalIconDefinition? selected = null;
        foreach (var candidate in catalog ?? Catalog)
        {
            if (candidate.DateRule.IsActive(date) && (selected == null || candidate.Priority > selected.Priority))
                selected = candidate;
        }

        return selected;
    }
}
