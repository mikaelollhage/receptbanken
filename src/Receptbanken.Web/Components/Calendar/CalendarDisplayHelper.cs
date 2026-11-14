using System.Globalization;
using Receptbanken.Core.Models;

namespace Receptbanken.Web.Components.Calendar;

public enum CalendarEditScope
{
    NewEvent,
    SingleOccurrence,
    EntireSeries
}

public enum CalendarRecurringAction
{
    Edit,
    Delete
}

public static class CalendarDisplayHelper
{
    public static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");

    private static readonly string[] ShortSwedishDayNames =
    [
        "Mån", "Tis", "Ons", "Tor", "Fre", "Lör", "Sön"
    ];

    public static string GetShortSwedishDayName(int dayIndex) => ShortSwedishDayNames[dayIndex];

    public static string FormatTimeRange(bool isAllDay, TimeOnly? startTime, TimeOnly? endTime)
    {
        if (isAllDay)
            return "Heldag";

        if (!startTime.HasValue)
            return string.Empty;

        var startText = startTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (!endTime.HasValue)
            return startText;

        var endText = endTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        return $"{startText}–{endText}";
    }

    public static string FormatReminderLabel(bool isAllDay, int? reminderMinutesBefore)
    {
        if (!reminderMinutesBefore.HasValue)
            return "Ingen påminnelse";

        if (isAllDay)
        {
            return reminderMinutesBefore.Value switch
            {
                CalendarReminderOptions.AllDaySameDay0900 => "Samma dag kl. 09:00",
                CalendarReminderOptions.AllDayPreviousEvening1800 => "Kvällen innan kl. 18:00",
                _ => $"{reminderMinutesBefore.Value} min innan"
            };
        }

        return reminderMinutesBefore.Value switch
        {
            CalendarReminderOptions.AtStart => "Vid start",
            CalendarReminderOptions.Minutes15 => "15 minuter innan",
            CalendarReminderOptions.Minutes30 => "30 minuter innan",
            CalendarReminderOptions.Hour1 => "1 timme innan",
            CalendarReminderOptions.Hours2 => "2 timmar innan",
            CalendarReminderOptions.Day1 => "1 dag innan",
            _ => $"{reminderMinutesBefore.Value} min innan"
        };
    }

    public static IReadOnlyList<(int Value, string Label)> GetReminderOptions(bool isAllDay)
    {
        var presets = isAllDay ? CalendarReminderOptions.AllDayPresets : CalendarReminderOptions.TimedPresets;
        return presets
            .Select(p => (p, FormatReminderLabel(isAllDay, p)))
            .ToList();
    }

    public static string FormatRecurrenceSummary(int intervalWeeks, IsoWeekdayMask mask)
    {
        if (intervalWeeks <= 0)
            return string.Empty;

        var intervalText = intervalWeeks == 1 ? "Varje vecka" : $"Var {intervalWeeks}:e vecka";
        var days = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            if ((mask & IsoWeekdayMaskExtensions.FromDayIndex(i)) != 0)
            {
                days.Add(ShortSwedishDayNames[i].ToLower(SwedishCulture));
            }
        }

        return days.Count > 0 ? $"{intervalText} ({string.Join(", ", days)})" : intervalText;
    }
}
