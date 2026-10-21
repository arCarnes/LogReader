namespace LogReader.Tests;

using LogReader.App.Services;

public sealed class SeasonalIconTests
{
    [Theory]
    [InlineData(2022, 31)] // Monday
    [InlineData(2023, 30)] // Tuesday
    [InlineData(2029, 29)] // Wednesday
    [InlineData(2024, 28)] // Thursday
    [InlineData(2025, 27)] // Friday
    [InlineData(2026, 26)] // Saturday
    [InlineData(2027, 25)] // Sunday
    public void Halloween_UsesTheEntireMondayThroughSundayWeek(int year, int mondayDay)
    {
        var monday = new DateOnly(year, 10, mondayDay);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        Assert.Null(SeasonalIcons.Resolve(monday.AddDays(-1)));
        for (var offset = 0; offset < 7; offset++)
            Assert.Equal("halloween", SeasonalIcons.Resolve(monday.AddDays(offset))?.Id);
        Assert.Null(SeasonalIcons.Resolve(monday.AddDays(7)));
        Assert.Null(SeasonalIcons.Resolve(new DateOnly(year, 12, 25)));
    }

    [Fact]
    public void HolidayWeek_HandlesAdjacentYearsLeapDaysAndCalendarExtremes()
    {
        var newYear = new HolidayWeekRule(1, 1);
        Assert.False(newYear.IsActive(new DateOnly(2021, 12, 26)));
        Assert.True(newYear.IsActive(new DateOnly(2021, 12, 27)));
        Assert.True(newYear.IsActive(new DateOnly(2022, 1, 2)));
        Assert.False(newYear.IsActive(new DateOnly(2022, 1, 3)));
        var yearEnd = new HolidayWeekRule(12, 31);
        Assert.True(yearEnd.IsActive(new DateOnly(2025, 1, 5)));
        Assert.False(yearEnd.IsActive(new DateOnly(2025, 1, 6)));
        Assert.True(newYear.IsActive(DateOnly.MinValue));
        Assert.True(yearEnd.IsActive(DateOnly.MaxValue));
        var leapDay = new HolidayWeekRule(2, 29);
        Assert.True(leapDay.IsActive(new DateOnly(2024, 2, 26)));
        Assert.False(leapDay.IsActive(new DateOnly(2025, 2, 28)));
    }

    [Theory]
    [InlineData(12, 19, false)]
    [InlineData(12, 20, true)]
    [InlineData(12, 25, true)]
    [InlineData(12, 26, false)]
    public void FixedRange_IncludesBothEndpoints(int month, int day, bool active)
        => Assert.Equal(active, new AnnualDateRangeRule(12, 20, 12, 25).IsActive(new DateOnly(2026, month, day)));

    [Theory]
    [InlineData(2026, 12, 23, false)]
    [InlineData(2026, 12, 24, true)]
    [InlineData(2026, 12, 31, true)]
    [InlineData(2027, 1, 1, true)]
    [InlineData(2027, 1, 6, true)]
    [InlineData(2027, 1, 7, false)]
    [InlineData(2027, 7, 1, false)]
    public void FixedRange_CrossesNewYear(int year, int month, int day, bool active)
        => Assert.Equal(active, new AnnualDateRangeRule(12, 24, 1, 6).IsActive(new DateOnly(year, month, day)));

    [Fact]
    public void FixedRange_EqualEndpointsSelectOneDayAndInvalidDatesAreRejected()
    {
        var range = new AnnualDateRangeRule(2, 29, 2, 29);
        Assert.True(range.IsActive(new DateOnly(2024, 2, 29)));
        Assert.False(range.IsActive(new DateOnly(2024, 2, 28)));
        Assert.False(range.IsActive(new DateOnly(2025, 3, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HolidayWeekRule(2, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnnualDateRangeRule(13, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnnualDateRangeRule(1, 1, 4, 31));
    }

    [Fact]
    public void Resolver_UsesPriorityThenDeclarationOrderAndSupportsAnotherSeason()
    {
        var halloween = SeasonalIcons.Catalog[0];
        var synthetic = new SeasonalIconDefinition("synthetic", "synthetic.ico", new AnnualDateRangeRule(10, 1, 12, 25), 1);
        var tie = synthetic with { Id = "tie" };
        var date = new DateOnly(2026, 10, 31);
        Assert.Same(synthetic, SeasonalIcons.Resolve(date, [halloween, synthetic, tie]));
        Assert.Same(tie, SeasonalIcons.Resolve(date, [tie, synthetic, halloween]));
        Assert.Same(synthetic, SeasonalIcons.Resolve(new DateOnly(2026, 12, 25), [halloween, synthetic]));
        Assert.Null(SeasonalIcons.Resolve(new DateOnly(2026, 12, 26), [halloween, synthetic]));
        Assert.Null(SeasonalIcons.Resolve(date, []));
    }
}
