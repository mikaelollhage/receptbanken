using FluentAssertions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Tests.Core;

public class CalendarRecurrenceExpanderTests
{
    [Fact]
    public void Expand_OneTimeAndAllDayEvents_ShouldSortAllDayBeforeTimedAndByStartTimeEndTimeTitle()
    {
        var date = new DateOnly(2026, 11, 16);
        var events = new List<CalendarEvent>
        {
            new()
            {
                Id = 1,
                Title = "Tandläkare",
                StartDate = date,
                IsAllDay = false,
                StartTime = new TimeOnly(14, 0),
                EndTime = new TimeOnly(15, 0)
            },
            new()
            {
                Id = 2,
                Title = "Födelsedag",
                StartDate = date,
                IsAllDay = true
            },
            new()
            {
                Id = 3,
                Title = "Morgonmöte",
                StartDate = date,
                IsAllDay = false,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(9, 30)
            },
            new()
            {
                Id = 4,
                Title = "Annan heldag",
                StartDate = date,
                IsAllDay = true
            }
        };

        var result = CalendarRecurrenceExpander.Expand(events, date, date);

        result.Select(o => o.Title).Should().Equal(
            "Annan heldag",
            "Födelsedag",
            "Morgonmöte",
            "Tandläkare");
        result.Take(2).Should().OnlyContain(o => o.IsAllDay);
        result.Skip(2).Should().OnlyContain(o => !o.IsAllDay);
    }

    [Theory]
    [InlineData(1, new[] { "2026-11-09", "2026-11-16", "2026-11-23", "2026-11-30" })]
    [InlineData(2, new[] { "2026-11-09", "2026-11-23" })]
    [InlineData(3, new[] { "2026-11-09", "2026-11-30" })]
    public void Expand_EveryNthWeek_ShouldEmitOnExpectedWeeks(int intervalWeeks, string[] expectedDates)
    {
        var ev = new CalendarEvent
        {
            Id = 10,
            Title = "Sophämtning",
            StartDate = new DateOnly(2026, 11, 9), // Monday week 46
            IsAllDay = true,
            RecurrenceIntervalWeeks = intervalWeeks,
            RecurrenceDaysOfWeekMask = (int)IsoWeekdayMask.Monday
        };

        var result = CalendarRecurrenceExpander.Expand(
            [ev],
            new DateOnly(2026, 11, 9),
            new DateOnly(2026, 12, 6));

        result.Select(o => o.Date.ToString("yyyy-MM-dd")).Should().Equal(expectedDates);
    }

    [Fact]
    public void Expand_MultipleWeekdaysAndStartEndDates_ShouldRespectBoundsAndMask()
    {
        var ev = new CalendarEvent
        {
            Id = 11,
            Title = "Träning",
            StartDate = new DateOnly(2026, 11, 11), // Wednesday
            IsAllDay = false,
            StartTime = new TimeOnly(18, 0),
            RecurrenceIntervalWeeks = 2,
            RecurrenceDaysOfWeekMask = (int)(IsoWeekdayMask.Monday | IsoWeekdayMask.Wednesday | IsoWeekdayMask.Friday),
            RecurrenceEndDate = new DateOnly(2026, 11, 27) // Friday two weeks later
        };

        var result = CalendarRecurrenceExpander.Expand(
            [ev],
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 12, 15));

        // Week 46 (2026-11-09..15): Mon 11-09 is BEFORE StartDate (11-11), so only Wed 11-11 and Fri 11-13.
        // Week 48 (2026-11-23..29): Mon 11-23, Wed 11-25, Fri 11-27 (inclusive RecurrenceEndDate).
        // Week 50: after RecurrenceEndDate.
        result.Select(o => o.Date).Should().Equal(
            new DateOnly(2026, 11, 11),
            new DateOnly(2026, 11, 13),
            new DateOnly(2026, 11, 23),
            new DateOnly(2026, 11, 25),
            new DateOnly(2026, 11, 27));
    }

    [Fact]
    public void Expand_AcrossIsoYearBoundaryWithWeek53_ShouldMaintainExactInterval()
    {
        // 2026 has 53 ISO weeks:
        // 2026-W52: 2026-12-21 .. 2026-12-27
        // 2026-W53: 2026-12-28 .. 2027-01-03
        // 2027-W01: 2027-01-04 .. 2027-01-10
        var ev = new CalendarEvent
        {
            Id = 12,
            Title = "Varannan tisdag",
            StartDate = new DateOnly(2026, 12, 22), // Tuesday W52
            IsAllDay = false,
            StartTime = new TimeOnly(17, 0),
            RecurrenceIntervalWeeks = 2,
            RecurrenceDaysOfWeekMask = (int)IsoWeekdayMask.Tuesday
        };

        var result = CalendarRecurrenceExpander.Expand(
            [ev],
            new DateOnly(2026, 12, 21),
            new DateOnly(2027, 1, 17));

        // Should hit 2026-12-22 (W52), skip 2026-12-29 (W53), hit 2027-01-05 (W01), skip 2027-01-12 (W02).
        result.Select(o => o.Date).Should().Equal(
            new DateOnly(2026, 12, 22),
            new DateOnly(2027, 1, 5));
    }

    [Fact]
    public void Expand_MovedOccurrenceAcrossWeeks_ShouldAppearOnlyOnTargetWeekWithoutDuplication()
    {
        var ev = new CalendarEvent
        {
            Id = 13,
            Title = "Musikskola",
            StartDate = new DateOnly(2026, 11, 10), // Tuesday W46
            IsAllDay = false,
            StartTime = new TimeOnly(16, 0),
            EndTime = new TimeOnly(17, 0),
            Location = "Kulturskolan",
            FamilyMembersCsv = "Erik",
            ReminderMinutesBefore = CalendarReminderOptions.Minutes30,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = (int)IsoWeekdayMask.Tuesday,
            Exceptions =
            [
                new CalendarEventException
                {
                    Id = 1,
                    CalendarEventId = 13,
                    OriginalDate = new DateOnly(2026, 11, 17), // Tuesday W47
                    IsCancelled = false,
                    OverrideDate = new DateOnly(2026, 11, 15), // Moved back to Sunday W46
                    OverrideTitle = "Musikskola (flyttad)",
                    OverrideStartTime = new TimeOnly(11, 0),
                    OverrideEndTime = null,
                    OverrideLocation = "",
                    OverrideReminderCleared = true
                }
            ]
        };

        var week46 = CalendarWeek.FromDate(new DateOnly(2026, 11, 10));
        var week46Occurrences = CalendarRecurrenceExpander.Expand([ev], week46.WeekStartDate, week46.WeekEndDate);

        week46Occurrences.Should().HaveCount(2);
        week46Occurrences[0].Date.Should().Be(new DateOnly(2026, 11, 10));
        week46Occurrences[0].IsException.Should().BeFalse();

        var moved = week46Occurrences[1];
        moved.Date.Should().Be(new DateOnly(2026, 11, 15));
        moved.OriginalDate.Should().Be(new DateOnly(2026, 11, 17));
        moved.IsException.Should().BeTrue();
        moved.IsMoved.Should().BeTrue();
        moved.Title.Should().Be("Musikskola (flyttad)");
        moved.StartTime.Should().Be(new TimeOnly(11, 0));
        moved.EndTime.Should().BeNull();
        moved.Location.Should().BeNull();
        moved.ReminderMinutesBefore.Should().BeNull();
        moved.FamilyMembers.Should().Equal("Erik");

        // Querying week 47 must NOT show the moved occurrence on Tuesday 2026-11-17.
        var week47 = week46.NextWeek();
        var week47Occurrences = CalendarRecurrenceExpander.Expand([ev], week47.WeekStartDate, week47.WeekEndDate);
        week47Occurrences.Should().BeEmpty();
    }

    [Theory]
    [InlineData("2026-11-18", "2026-11-25", "2026-11-25", "2026-12-02", "2026-11-30", "2026-12-06", new[] { "2026-12-02" })]
    [InlineData("2026-11-18", "2026-11-25", "2026-11-25", "2026-12-02", "2026-11-23", "2026-11-29", new string[0])]
    [InlineData("2026-11-18", "2026-11-25", "2026-11-18", "2026-11-15", "2026-11-09", "2026-11-15", new[] { "2026-11-15" })]
    [InlineData("2026-12-23", null, "2026-12-30", "2027-01-02", "2026-12-28", "2026-12-31", new string[0])]
    [InlineData("2026-12-23", null, "2026-12-30", "2027-01-02", "2027-01-01", "2027-01-03", new[] { "2027-01-02" })]
    [InlineData("2026-11-18", "2026-11-25", "2026-12-02", "2026-11-26", "2026-11-23", "2026-11-29", new[] { "2026-11-25" })]
    public void Expand_MovedOccurrenceAcrossDateBoundariesAndRecurrenceEndDate_ShouldRespectBounds(
        string startDateStr,
        string? recurrenceEndDateStr,
        string originalDateStr,
        string overrideDateStr,
        string rangeStartStr,
        string rangeEndStr,
        string[] expectedDates)
    {
        var ev = new CalendarEvent
        {
            Id = 15,
            Title = "Onsdagsklubben",
            StartDate = DateOnly.Parse(startDateStr),
            IsAllDay = true,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = (int)IsoWeekdayMask.Wednesday,
            RecurrenceEndDate = recurrenceEndDateStr is null ? null : DateOnly.Parse(recurrenceEndDateStr),
            Exceptions =
            [
                new CalendarEventException
                {
                    Id = 3,
                    CalendarEventId = 15,
                    OriginalDate = DateOnly.Parse(originalDateStr),
                    IsCancelled = false,
                    OverrideDate = DateOnly.Parse(overrideDateStr)
                }
            ]
        };

        var result = CalendarRecurrenceExpander.Expand(
            [ev],
            DateOnly.Parse(rangeStartStr),
            DateOnly.Parse(rangeEndStr));

        result.Select(o => o.Date.ToString("yyyy-MM-dd")).Should().Equal(expectedDates);
    }

    [Fact]
    public void Expand_CancelledOccurrence_ShouldNotAffectOtherOccurrences()
    {
        var ev = new CalendarEvent
        {
            Id = 14,
            Title = "Simning",
            StartDate = new DateOnly(2026, 11, 12), // Thursday
            IsAllDay = false,
            StartTime = new TimeOnly(18, 30),
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = (int)IsoWeekdayMask.Thursday,
            Exceptions =
            [
                new CalendarEventException
                {
                    Id = 2,
                    CalendarEventId = 14,
                    OriginalDate = new DateOnly(2026, 11, 19),
                    IsCancelled = true
                }
            ]
        };

        var result = CalendarRecurrenceExpander.Expand(
            [ev],
            new DateOnly(2026, 11, 12),
            new DateOnly(2026, 11, 26));

        result.Select(o => o.Date).Should().Equal(
            new DateOnly(2026, 11, 12),
            new DateOnly(2026, 11, 26));
    }

    [Theory]
    [InlineData(CalendarReminderOptions.AllDaySameDay0900, "2026-11-20 09:00")]
    [InlineData(CalendarReminderOptions.AllDayPreviousEvening1800, "2026-11-19 18:00")]
    public void CalendarReminderOptions_AllDayReminders_ShouldComputeExpectedLocalDateTime(
        int minutesOffset,
        string expectedLocalDateTime)
    {
        var actual = CalendarReminderOptions.GetReminderLocalDateTime(
            new DateOnly(2026, 11, 20),
            isAllDay: true,
            startTime: null,
            reminderMinutesBefore: minutesOffset);

        actual.Should().NotBeNull();
        actual!.Value.ToString("yyyy-MM-dd HH:mm").Should().Be(expectedLocalDateTime);
    }

    [Theory]
    [InlineData(CalendarReminderOptions.AtStart, "2026-11-20 18:00")]
    [InlineData(CalendarReminderOptions.Minutes15, "2026-11-20 17:45")]
    [InlineData(CalendarReminderOptions.Minutes30, "2026-11-20 17:30")]
    [InlineData(CalendarReminderOptions.Hour1, "2026-11-20 17:00")]
    [InlineData(CalendarReminderOptions.Hours2, "2026-11-20 16:00")]
    [InlineData(CalendarReminderOptions.Day1, "2026-11-19 18:00")]
    public void CalendarReminderOptions_TimedReminders_ShouldComputeExpectedLocalDateTime(
        int minutesBefore,
        string expectedLocalDateTime)
    {
        var actual = CalendarReminderOptions.GetReminderLocalDateTime(
            new DateOnly(2026, 11, 20),
            isAllDay: false,
            startTime: new TimeOnly(18, 0),
            reminderMinutesBefore: minutesBefore);

        actual.Should().NotBeNull();
        actual!.Value.ToString("yyyy-MM-dd HH:mm").Should().Be(expectedLocalDateTime);
    }
}
