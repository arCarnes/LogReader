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
    public void HolidayWeek_UsesTheEntireMondayThroughSundayWeek(int year, int mondayDay)
    {
        var rule = new HolidayWeekRule(10, 31);
        var monday = new DateOnly(year, 10, mondayDay);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        Assert.False(rule.IsActive(monday.AddDays(-1)));
        for (var offset = 0; offset < 7; offset++)
            Assert.True(rule.IsActive(monday.AddDays(offset)));
        Assert.False(rule.IsActive(monday.AddDays(7)));
        Assert.False(rule.IsActive(new DateOnly(year, 12, 25)));
    }

    [Fact]
    public void Catalog_UsesFiveDaysOfLeadUpAndOneDayAfterAcrossTheGregorianCycle()
    {
        Assert.Equal(new[] { "halloween", "thanksgiving", "christmas", "new-year" },
            SeasonalIcons.Catalog.Select(icon => icon.Id));
        for (var year = 2000; year < 2400; year++)
        {
            // The fourth Thursday is the single Thursday among November 22–28.
            var thanksgiving = Enumerable.Range(22, 7).Select(day => new DateOnly(year, 11, day))
                .Single(date => date.DayOfWeek == DayOfWeek.Thursday);
            var anchors = new[] { new DateOnly(year, 10, 31), thanksgiving,
                new DateOnly(year, 12, 25), new DateOnly(year, 1, 1) };
            for (var index = 0; index < anchors.Length; index++)
            {
                var definition = SeasonalIcons.Catalog[index];
                for (var offset = -6; offset <= 2; offset++)
                {
                    var date = anchors[index].AddDays(offset);
                    var active = offset is >= -5 and <= 1;
                    Assert.Equal(active, definition.DateRule.IsActive(date));
                    if (active)
                        Assert.Equal(definition.Id, SeasonalIcons.Resolve(date)?.Id);
                    else
                        Assert.NotEqual(definition.Id, SeasonalIcons.Resolve(date)?.Id);
                }
            }
            Assert.Equal("christmas", SeasonalIcons.Resolve(new DateOnly(year, 12, 26))?.Id);
            Assert.Equal("new-year", SeasonalIcons.Resolve(new DateOnly(year, 12, 27))?.Id);
            Assert.Null(SeasonalIcons.Resolve(new DateOnly(year, 1, 3)));
            Assert.Null(SeasonalIcons.Resolve(new DateOnly(year, 7, 1)));
        }
    }

    [Fact]
    public void HolidayWindow_HandlesLeapDaysMissingWeekdaysAndCalendarLimits()
    {
        var leapDay = new HolidayWindowRule(2, 29);
        Assert.False(leapDay.IsActive(new DateOnly(2024, 2, 23)));
        Assert.True(leapDay.IsActive(new DateOnly(2024, 2, 24)));
        Assert.True(leapDay.IsActive(new DateOnly(2024, 3, 1)));
        Assert.False(leapDay.IsActive(new DateOnly(2024, 3, 2)));
        Assert.False(leapDay.IsActive(new DateOnly(2025, 2, 28)));
        Assert.False(leapDay.IsActive(new DateOnly(2100, 2, 28)));
        var fifthMonday = new HolidayWindowRule(2, DayOfWeek.Monday, 5);
        Assert.True(fifthMonday.IsActive(new DateOnly(2016, 2, 29)));
        Assert.True(fifthMonday.IsActive(new DateOnly(2016, 3, 1)));
        Assert.False(fifthMonday.IsActive(new DateOnly(2026, 3, 2)));
        var newYear = new HolidayWindowRule(1, 1);
        Assert.True(newYear.IsActive(DateOnly.MinValue));
        Assert.True(newYear.IsActive(new DateOnly(1, 1, 2)));
        Assert.False(newYear.IsActive(new DateOnly(1, 1, 3)));
        Assert.False(newYear.IsActive(DateOnly.MaxValue)); // No year 10000 holiday.
        var yearEnd = new HolidayWindowRule(12, 31);
        Assert.True(yearEnd.IsActive(DateOnly.MaxValue));
        Assert.True(yearEnd.IsActive(new DateOnly(2027, 1, 1)));
        Assert.False(yearEnd.IsActive(new DateOnly(2027, 1, 2)));
    }

    [Fact]
    public void HolidayWindow_RejectsInvalidAnchors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HolidayWindowRule(2, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HolidayWindowRule(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HolidayWindowRule(13, DayOfWeek.Thursday, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HolidayWindowRule(11, (DayOfWeek)(-1), 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HolidayWindowRule(11, (DayOfWeek)7, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HolidayWindowRule(11, DayOfWeek.Thursday, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HolidayWindowRule(11, DayOfWeek.Thursday, 6));
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
