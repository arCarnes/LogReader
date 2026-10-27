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

internal sealed class HolidayWindowRule : ISeasonalIconDateRule
{
    private readonly int _month;
    private readonly int _day;
    private readonly DayOfWeek _weekday;
    private readonly int _occurrence;

    internal HolidayWindowRule(int month, int day)
    {
        _ = new DateOnly(2000, month, day);
        _month = month;
        _day = day;
    }

    internal HolidayWindowRule(int month, DayOfWeek weekday, int occurrence)
    {
        _ = new DateOnly(2000, month, 1);
        if (!Enum.IsDefined(weekday))
            throw new ArgumentOutOfRangeException(nameof(weekday));
        if (occurrence is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(occurrence));
        _month = month;
        _weekday = weekday;
        _occurrence = occurrence;
    }

    public bool IsActive(DateOnly date)
    {
        // New Year's lead-up belongs to the following anchor year.
        for (var year = Math.Max(1, date.Year - 1); year <= Math.Min(9999, date.Year + 1); year++)
        {
            var first = new DateOnly(year, _month, 1);
            var day = _occurrence == 0 ? _day
                : 1 + ((int)_weekday - (int)first.DayOfWeek + 7) % 7 + (_occurrence - 1) * 7;
            if (day > DateTime.DaysInMonth(year, _month))
                continue; // Skip absent leap days or fifth weekdays.

            var holiday = first.DayNumber + day - 1;
            var offset = date.DayNumber - holiday;
            if (offset is >= -5 and <= 1)
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
    internal const string ThanksgivingIcon = "pack://application:,,,/WeezTail;component/Assets/Seasonal/thanksgiving.ico";
    internal const string ChristmasIcon = "pack://application:,,,/WeezTail;component/Assets/Seasonal/christmas.ico";
    internal const string NewYearIcon = "pack://application:,,,/WeezTail;component/Assets/Seasonal/new-year.ico";

    internal static IReadOnlyList<SeasonalIconDefinition> Catalog { get; } = Array.AsReadOnly(
        new[]
        {
            new SeasonalIconDefinition("halloween", HalloweenIcon, new HolidayWindowRule(10, 31)),
            new SeasonalIconDefinition("thanksgiving", ThanksgivingIcon, new HolidayWindowRule(11, DayOfWeek.Thursday, 4)),
            new SeasonalIconDefinition("christmas", ChristmasIcon, new HolidayWindowRule(12, 25)),
            new SeasonalIconDefinition("new-year", NewYearIcon, new HolidayWindowRule(1, 1))
        });

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
