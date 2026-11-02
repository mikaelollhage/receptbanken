using FluentAssertions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Tests.Core;

public class CalendarWeekTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    [Fact]
    public void FromDate_RegularWeek_ShouldResolveCorrectMondaySundayAndIsoWeek()
    {
        // Wednesday 28 October 2026 is in ISO Week 44 of 2026 (Mon 26 Oct – Sun 1 Nov)
        var date = new DateOnly(2026, 10, 28);
        var week = CalendarWeek.FromDate(date);

        week.IsoYear.Should().Be(2026);
        week.IsoWeek.Should().Be(44);
        week.WeekStartDate.Should().Be(new DateOnly(2026, 10, 26));
        week.WeekEndDate.Should().Be(new DateOnly(2026, 11, 1));
        week.WeekStartDate.DayOfWeek.Should().Be(DayOfWeek.Monday);
        week.WeekEndDate.DayOfWeek.Should().Be(DayOfWeek.Sunday);
        week.GetDates().Should().HaveCount(7);
    }

    [Fact]
    public void FromDate_YearBoundary_EndOf2025BelongingToIsoWeek1Of2026_ShouldBeHandledCorrectly()
    {
        // Monday 29 Dec 2025 through Sunday 4 Jan 2026 is ISO Week 1 of 2026
        var dec29 = new DateOnly(2025, 12, 29);
        var dec31 = new DateOnly(2025, 12, 31);
        var jan04 = new DateOnly(2026, 1, 4);

        var weekFromDec29 = CalendarWeek.FromDate(dec29);
        var weekFromDec31 = CalendarWeek.FromDate(dec31);
        var weekFromJan04 = CalendarWeek.FromDate(jan04);

        weekFromDec29.IsoYear.Should().Be(2026);
        weekFromDec29.IsoWeek.Should().Be(1);
        weekFromDec29.WeekStartDate.Should().Be(new DateOnly(2025, 12, 29));
        weekFromDec29.WeekEndDate.Should().Be(new DateOnly(2026, 1, 4));

        weekFromDec31.Should().Be(weekFromDec29);
        weekFromJan04.Should().Be(weekFromDec29);
    }

    [Fact]
    public void FromDate_IsoWeek1_StartingInPreviousCalendarYear_ShouldResolveCorrectly()
    {
        // 1 Jan 2025 is a Wednesday; ISO Week 1 of 2025 starts Monday 30 Dec 2024
        var jan1 = new DateOnly(2025, 1, 1);
        var week = CalendarWeek.FromDate(jan1);

        week.IsoYear.Should().Be(2025);
        week.IsoWeek.Should().Be(1);
        week.WeekStartDate.Should().Be(new DateOnly(2024, 12, 30));
        week.WeekEndDate.Should().Be(new DateOnly(2025, 1, 5));
    }

    [Fact]
    public void FromDate_And_FromIsoWeek_Week53_ShouldBeHandledCorrectlyWhenPresent()
    {
        // 2026 has 53 ISO weeks: Week 53 of 2026 runs Mon 28 Dec 2026 – Sun 3 Jan 2027
        var jan2_2027 = new DateOnly(2027, 1, 2);
        var week53 = CalendarWeek.FromDate(jan2_2027);

        week53.IsoYear.Should().Be(2026);
        week53.IsoWeek.Should().Be(53);
        week53.WeekStartDate.Should().Be(new DateOnly(2026, 12, 28));
        week53.WeekEndDate.Should().Be(new DateOnly(2027, 1, 3));

        var constructed = CalendarWeek.FromIsoWeek(2026, 53);
        constructed.Should().Be(week53);

        // Next week after 2026-W53 is 2027-W01 starting Monday 4 Jan 2027
        var next = week53.NextWeek();
        next.IsoYear.Should().Be(2027);
        next.IsoWeek.Should().Be(1);
        next.WeekStartDate.Should().Be(new DateOnly(2027, 1, 4));

        // 2025 only has 52 ISO weeks, so requesting week 53 of 2025 must throw
        var act = () => CalendarWeek.FromIsoWeek(2025, 53);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SwedishLocalDateProvider_ShouldUseLocalCalendarDateNotRawUtcAtMidnightBoundary()
    {
        // Sunday 2026-11-01 23:30 UTC is already Monday 2026-11-02 00:30 in Sweden (CET, UTC+1)
        var utcSundayNight = new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.Zero);
        var stockholmTz = TimeZoneInfo.CreateCustomTimeZone("CET-Test", TimeSpan.FromHours(1), "CET", "CET");

        var localDate = SwedishLocalDateProvider.FromUtcInstant(utcSundayNight, stockholmTz);
        localDate.Should().Be(new DateOnly(2026, 11, 2));
        localDate.DayOfWeek.Should().Be(DayOfWeek.Monday);

        var week = CalendarWeek.FromDate(localDate);
        week.IsoYear.Should().Be(2026);
        week.IsoWeek.Should().Be(45);

        // Client override date takes precedence when set by browser
        var provider = new SwedishLocalDateProvider(new FakeTimeProvider(utcSundayNight));
        provider.SetClientLocalDate(new DateOnly(2026, 11, 5));
        provider.GetLocalToday().Should().Be(new DateOnly(2026, 11, 5));
        CalendarWeek.GetSwedishDayName(provider.GetLocalToday()).Should().Be("Torsdag");
    }

    [Theory]
    [InlineData(0, "Vardagsmat")]
    [InlineData(1, "Vardagsmat")]
    [InlineData(2, "Vardagsmat")]
    [InlineData(3, "Vardagsmat")]
    [InlineData(4, "Fest & Helg")]
    [InlineData(5, "Fest & Helg")]
    [InlineData(6, "Vardagsmat")]
    public void GetDefaultCategoryName_ShouldMatchSpecificationC1(int dayIndex, string expectedCategory)
    {
        CalendarWeek.GetDefaultCategoryName(dayIndex).Should().Be(expectedCategory);
    }
}

