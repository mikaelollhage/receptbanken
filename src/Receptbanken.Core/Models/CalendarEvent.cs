using System.ComponentModel.DataAnnotations.Schema;

namespace Receptbanken.Core.Models;

/// <summary>
/// Represents a one-time family calendar event or the recurrence rule for a recurring series.
/// Dates and times are stored in local wall-clock time (Europe/Stockholm) rather than fixed UTC instants.
/// </summary>
public class CalendarEvent
{
    public const int MaxTitleLength = 200;
    public const int MaxLocationLength = 200;
    public const int MaxNotesLength = 2000;
    public const int MaxFamilyMembersCsvLength = 300;
    public const int MaxRecurrenceIntervalWeeks = 52;

    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// For one-time events, the event date. For recurring series, the first date on or after which the series is active.
    /// </summary>
    public DateOnly StartDate { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>
    /// Required when <see cref="IsAllDay"/> is false; null when <see cref="IsAllDay"/> is true.
    /// </summary>
    public TimeOnly? StartTime { get; set; }

    /// <summary>
    /// Optional end time for timed single-day events. When set, must be greater than <see cref="StartTime"/>.
    /// </summary>
    public TimeOnly? EndTime { get; set; }

    public string? Location { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Normalized comma-separated list of family member names (e.g. "Anna,Erik").
    /// </summary>
    public string? FamilyMembersCsv { get; set; }

    /// <summary>
    /// Reminder offset in minutes. Null means no reminder.
    /// For timed events: minutes before <see cref="StartTime"/> (0, 15, 30, 60, 120, 1440).
    /// For all-day events: minutes before 09:00 on the event day (0 = 09:00 same day, 900 = 18:00 previous evening).
    /// </summary>
    public int? ReminderMinutesBefore { get; set; }

    /// <summary>
    /// 0 for one-time events; 1..52 for recurring every Nth week.
    /// </summary>
    public int RecurrenceIntervalWeeks { get; set; }

    /// <summary>
    /// Bitmask of <see cref="IsoWeekdayMask"/> (Monday = 1 .. Sunday = 64). 0 for one-time events.
    /// </summary>
    public int RecurrenceDaysOfWeekMask { get; set; }

    /// <summary>
    /// Optional inclusive end date for recurring series. Null means no end date.
    /// </summary>
    public DateOnly? RecurrenceEndDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<CalendarEventException> Exceptions { get; set; } = new();

    [NotMapped]
    public IReadOnlyList<string> FamilyMembers
    {
        get => CalendarFamilyMemberHelper.ParseCsv(FamilyMembersCsv);
        set => FamilyMembersCsv = CalendarFamilyMemberHelper.FormatCsv(value);
    }
}

/// <summary>
/// Defines supported reminder presets and local trigger calculation for timed and all-day events.
/// </summary>
public static class CalendarReminderOptions
{
    public const int AtStart = 0;
    public const int Minutes15 = 15;
    public const int Minutes30 = 30;
    public const int Hour1 = 60;
    public const int Hours2 = 120;
    public const int Day1 = 1440;

    /// <summary>
    /// All-day event reminder at 09:00 on the event day (0 minutes before 09:00).
    /// </summary>
    public const int AllDaySameDay0900 = 0;

    /// <summary>
    /// All-day event reminder at 18:00 the evening before (900 minutes before 09:00 on the event day).
    /// </summary>
    public const int AllDayPreviousEvening1800 = 900;

    public static readonly TimeOnly AllDayAnchorTime = new(9, 0);

    public static readonly IReadOnlyList<int> TimedPresets =
        [AtStart, Minutes15, Minutes30, Hour1, Hours2, Day1];

    public static readonly IReadOnlyList<int> AllDayPresets =
        [AllDaySameDay0900, AllDayPreviousEvening1800];

    public static bool IsValidReminder(bool isAllDay, int? reminderMinutesBefore)
    {
        if (!reminderMinutesBefore.HasValue)
            return true;

        var allowed = isAllDay ? AllDayPresets : TimedPresets;
        return allowed.Contains(reminderMinutesBefore.Value);
    }

    /// <summary>
    /// Computes the local wall-clock <see cref="DateTime"/> (Kind = Unspecified) when a reminder should fire,
    /// or null when <paramref name="reminderMinutesBefore"/> is null.
    /// </summary>
    public static DateTime? GetReminderLocalDateTime(
        DateOnly occurrenceDate,
        bool isAllDay,
        TimeOnly? startTime,
        int? reminderMinutesBefore)
    {
        if (!reminderMinutesBefore.HasValue)
            return null;

        if (!IsValidReminder(isAllDay, reminderMinutesBefore))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderMinutesBefore),
                $"Reminder value '{reminderMinutesBefore.Value}' is not valid for {(isAllDay ? "all-day" : "timed")} events.");
        }

        if (isAllDay)
        {
            var anchor = occurrenceDate.ToDateTime(AllDayAnchorTime, DateTimeKind.Unspecified);
            return anchor.AddMinutes(-reminderMinutesBefore.Value);
        }

        if (!startTime.HasValue)
            throw new InvalidOperationException("A timed event must have a StartTime to compute a reminder time.");

        var eventStart = occurrenceDate.ToDateTime(startTime.Value, DateTimeKind.Unspecified);
        return eventStart.AddMinutes(-reminderMinutesBefore.Value);
    }
}
