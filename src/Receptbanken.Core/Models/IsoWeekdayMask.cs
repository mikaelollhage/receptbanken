using System.ComponentModel.DataAnnotations.Schema;

namespace Receptbanken.Core.Models;

/// <summary>
/// Represents a 7-bit weekday bitmask aligned with <see cref="CalendarWeek.GetDayIndex"/> (Monday = 0 .. Sunday = 6).
/// </summary>
[Flags]
public enum IsoWeekdayMask
{
    None      = 0,
    Monday    = 1 << 0, // 1
    Tuesday   = 1 << 1, // 2
    Wednesday = 1 << 2, // 4
    Thursday  = 1 << 3, // 8
    Friday    = 1 << 4, // 16
    Saturday  = 1 << 5, // 32
    Sunday    = 1 << 6, // 64
    All       = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday // 127
}

public static class IsoWeekdayMaskExtensions
{
    public static IsoWeekdayMask FromDayIndex(int dayIndex)
    {
        if (dayIndex < 0 || dayIndex > 6)
            throw new ArgumentOutOfRangeException(nameof(dayIndex), "Day index must be between 0 (Monday) and 6 (Sunday).");

        return (IsoWeekdayMask)(1 << dayIndex);
    }

    public static IsoWeekdayMask FromDate(DateOnly date) =>
        FromDayIndex(CalendarWeek.GetDayIndex(date));

    public static bool ContainsDate(this IsoWeekdayMask mask, DateOnly date) =>
        (mask & FromDate(date)) != 0;

    public static bool HasValidWeekdaysOnly(this IsoWeekdayMask mask) =>
        ((int)mask & ~(int)IsoWeekdayMask.All) == 0;
}
