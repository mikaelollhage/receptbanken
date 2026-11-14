using Receptbanken.Core.Models;

namespace Receptbanken.Core.Services;

/// <summary>
/// Deterministically expands one-time and recurring <see cref="CalendarEvent"/> rules
/// (with <see cref="CalendarEventException"/> overrides/cancellations) over a bounded date range.
/// </summary>
public static class CalendarRecurrenceExpander
{
    /// <summary>
    /// Checks whether <paramref name="date"/> falls on a valid recurrence occurrence of <paramref name="ev"/>
    /// according to its start date, optional end date, week interval, and weekday mask.
    /// </summary>
    public static bool IsValidRecurrenceDate(CalendarEvent ev, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(ev);

        if (ev.RecurrenceIntervalWeeks <= 0)
            return date == ev.StartDate;

        if (date < ev.StartDate)
            return false;

        if (ev.RecurrenceEndDate.HasValue && date > ev.RecurrenceEndDate.Value)
            return false;

        var seriesStartMonday = CalendarWeek.FromDate(ev.StartDate).WeekStartDate;
        var candidateMonday = CalendarWeek.FromDate(date).WeekStartDate;
        var weeksElapsed = CalendarWeek.WeeksBetween(seriesStartMonday, candidateMonday);

        if (weeksElapsed < 0 || weeksElapsed % ev.RecurrenceIntervalWeeks != 0)
            return false;

        var dayBit = 1 << CalendarWeek.GetDayIndex(date);
        return (ev.RecurrenceDaysOfWeekMask & dayBit) != 0;
    }

    /// <summary>
    /// Expands all <paramref name="events"/> within [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>]
    /// and sorts occurrences by date, all-day first, start time, end time, and title.
    /// </summary>
    public static IReadOnlyList<CalendarOccurrenceDto> Expand(
        IEnumerable<CalendarEvent> events,
        DateOnly rangeStart,
        DateOnly rangeEnd)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (rangeEnd < rangeStart)
            throw new ArgumentException("Slutdatum måste vara samma som eller senare än startdatum.", nameof(rangeEnd));

        var occurrences = new List<CalendarOccurrenceDto>();
        foreach (var ev in events)
        {
            ExpandEventInto(ev, rangeStart, rangeEnd, occurrences);
        }

        return SortOccurrences(occurrences);
    }

    /// <summary>
    /// Expands a single <see cref="CalendarEvent"/> within [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>].
    /// </summary>
    public static IReadOnlyList<CalendarOccurrenceDto> ExpandEvent(
        CalendarEvent ev,
        DateOnly rangeStart,
        DateOnly rangeEnd)
    {
        ArgumentNullException.ThrowIfNull(ev);

        if (rangeEnd < rangeStart)
            throw new ArgumentException("Slutdatum måste vara samma som eller senare än startdatum.", nameof(rangeEnd));

        var occurrences = new List<CalendarOccurrenceDto>();
        ExpandEventInto(ev, rangeStart, rangeEnd, occurrences);
        return SortOccurrences(occurrences);
    }

    private static void ExpandEventInto(
        CalendarEvent ev,
        DateOnly rangeStart,
        DateOnly rangeEnd,
        List<CalendarOccurrenceDto> output)
    {
        if (ev.RecurrenceIntervalWeeks <= 0)
        {
            if (ev.StartDate >= rangeStart && ev.StartDate <= rangeEnd)
            {
                output.Add(BuildOccurrence(ev, exception: null, originalDate: ev.StartDate, displayDate: ev.StartDate));
            }
            return;
        }

        var exceptionsByOriginalDate = ev.Exceptions
            .GroupBy(ex => ex.OriginalDate)
            .ToDictionary(g => g.Key, g => g.First());

        var emittedExceptionDates = new HashSet<DateOnly>();

        var effectiveStart = ev.StartDate > rangeStart ? ev.StartDate : rangeStart;
        var effectiveEnd = ev.RecurrenceEndDate.HasValue && ev.RecurrenceEndDate.Value < rangeEnd
            ? ev.RecurrenceEndDate.Value
            : rangeEnd;

        if (effectiveStart <= effectiveEnd)
        {
            var seriesStartMonday = CalendarWeek.FromDate(ev.StartDate).WeekStartDate;

            for (var d = effectiveStart; d <= effectiveEnd; d = d.AddDays(1))
            {
                var candidateMonday = CalendarWeek.FromDate(d).WeekStartDate;
                var weeksElapsed = CalendarWeek.WeeksBetween(seriesStartMonday, candidateMonday);
                if (weeksElapsed < 0 || weeksElapsed % ev.RecurrenceIntervalWeeks != 0)
                    continue;

                var dayBit = 1 << CalendarWeek.GetDayIndex(d);
                if ((ev.RecurrenceDaysOfWeekMask & dayBit) == 0)
                    continue;

                if (exceptionsByOriginalDate.TryGetValue(d, out var ex))
                {
                    emittedExceptionDates.Add(d);

                    if (ex.IsCancelled)
                        continue;

                    var displayDate = ex.OverrideDate ?? d;
                    if (displayDate >= rangeStart && displayDate <= rangeEnd)
                    {
                        output.Add(BuildOccurrence(ev, ex, originalDate: d, displayDate: displayDate));
                    }
                }
                else
                {
                    output.Add(BuildOccurrence(ev, exception: null, originalDate: d, displayDate: d));
                }
            }
        }

        // Include occurrences moved into [rangeStart, rangeEnd] from an OriginalDate outside the query window.
        foreach (var ex in ev.Exceptions)
        {
            if (ex.IsCancelled || !ex.OverrideDate.HasValue)
                continue;

            var targetDate = ex.OverrideDate.Value;
            if (targetDate < rangeStart || targetDate > rangeEnd)
                continue;

            if (emittedExceptionDates.Contains(ex.OriginalDate))
                continue;

            if (!IsValidRecurrenceDate(ev, ex.OriginalDate))
                continue;

            emittedExceptionDates.Add(ex.OriginalDate);
            output.Add(BuildOccurrence(ev, ex, originalDate: ex.OriginalDate, displayDate: targetDate));
        }
    }

    private static CalendarOccurrenceDto BuildOccurrence(
        CalendarEvent ev,
        CalendarEventException? exception,
        DateOnly originalDate,
        DateOnly displayDate)
    {
        var isRecurring = ev.RecurrenceIntervalWeeks >= 1;

        if (exception == null)
        {
            return new CalendarOccurrenceDto
            {
                CalendarEventId = ev.Id,
                Date = displayDate,
                OriginalDate = originalDate,
                IsRecurring = isRecurring,
                IsException = false,
                IsMoved = false,
                Title = ev.Title,
                IsAllDay = ev.IsAllDay,
                StartTime = ev.IsAllDay ? null : ev.StartTime,
                EndTime = ev.IsAllDay ? null : ev.EndTime,
                Location = ev.Location,
                Notes = ev.Notes,
                FamilyMembers = ev.FamilyMembers,
                ReminderMinutesBefore = ev.ReminderMinutesBefore,
                RecurrenceIntervalWeeks = ev.RecurrenceIntervalWeeks,
                RecurrenceDaysOfWeekMask = (IsoWeekdayMask)ev.RecurrenceDaysOfWeekMask,
                RecurrenceEndDate = ev.RecurrenceEndDate
            };
        }

        var isAllDay = exception.OverrideIsAllDay ?? ev.IsAllDay;
        TimeOnly? startTime;
        TimeOnly? endTime;

        if (isAllDay)
        {
            startTime = null;
            endTime = null;
        }
        else if (exception.OverrideStartTime.HasValue)
        {
            startTime = exception.OverrideStartTime.Value;
            endTime = exception.OverrideEndTime;
        }
        else
        {
            startTime = ev.StartTime;
            endTime = ev.EndTime;
        }

        var location = exception.OverrideLocation != null
            ? (string.IsNullOrWhiteSpace(exception.OverrideLocation) ? null : exception.OverrideLocation)
            : ev.Location;

        var notes = exception.OverrideNotes != null
            ? (string.IsNullOrWhiteSpace(exception.OverrideNotes) ? null : exception.OverrideNotes)
            : ev.Notes;

        var familyMembers = exception.OverrideFamilyMembersCsv != null
            ? CalendarFamilyMemberHelper.ParseCsv(exception.OverrideFamilyMembersCsv)
            : ev.FamilyMembers;

        int? reminderMinutesBefore;
        if (exception.OverrideReminderCleared)
        {
            reminderMinutesBefore = null;
        }
        else if (exception.OverrideReminderMinutesBefore.HasValue)
        {
            reminderMinutesBefore = exception.OverrideReminderMinutesBefore.Value;
        }
        else if (exception.OverrideIsAllDay.HasValue && exception.OverrideIsAllDay.Value != ev.IsAllDay)
        {
            reminderMinutesBefore = null;
        }
        else
        {
            reminderMinutesBefore = ev.ReminderMinutesBefore;
        }

        return new CalendarOccurrenceDto
        {
            CalendarEventId = ev.Id,
            Date = displayDate,
            OriginalDate = originalDate,
            IsRecurring = isRecurring,
            IsException = true,
            IsMoved = displayDate != originalDate,
            Title = exception.OverrideTitle ?? ev.Title,
            IsAllDay = isAllDay,
            StartTime = startTime,
            EndTime = endTime,
            Location = location,
            Notes = notes,
            FamilyMembers = familyMembers,
            ReminderMinutesBefore = reminderMinutesBefore,
            RecurrenceIntervalWeeks = ev.RecurrenceIntervalWeeks,
            RecurrenceDaysOfWeekMask = (IsoWeekdayMask)ev.RecurrenceDaysOfWeekMask,
            RecurrenceEndDate = ev.RecurrenceEndDate
        };
    }

    private static IReadOnlyList<CalendarOccurrenceDto> SortOccurrences(List<CalendarOccurrenceDto> occurrences) =>
        occurrences
            .OrderBy(o => o.Date)
            .ThenByDescending(o => o.IsAllDay)
            .ThenBy(o => o.StartTime)
            .ThenBy(o => o.EndTime)
            .ThenBy(o => o.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.CalendarEventId)
            .ToList();
}
