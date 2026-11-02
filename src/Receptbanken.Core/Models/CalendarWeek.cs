using System.Globalization;

namespace Receptbanken.Core.Models;

/// <summary>
/// Represents an ISO 8601 calendar week (Monday through Sunday).
/// Handles year-boundary weeks, week 1, and week 53 accurately via <see cref="ISOWeek"/>.
/// </summary>
public readonly record struct CalendarWeek
{
    private static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");

    private static readonly string[] SwedishDayNames =
    [
        "Måndag",
        "Tisdag",
        "Onsdag",
        "Torsdag",
        "Fredag",
        "Lördag",
        "Söndag"
    ];

    public int IsoYear { get; }
    public int IsoWeek { get; }
    public DateOnly WeekStartDate { get; }
    public DateOnly WeekEndDate => WeekStartDate.AddDays(6);

    public CalendarWeek(int isoYear, int isoWeek, DateOnly weekStartDate)
    {
        IsoYear = isoYear;
        IsoWeek = isoWeek;
        WeekStartDate = weekStartDate;
    }

    /// <summary>
    /// Creates an ISO 8601 <see cref="CalendarWeek"/> from any local calendar date.
    /// </summary>
    public static CalendarWeek FromDate(DateOnly date)
    {
        var dt = date.ToDateTime(TimeOnly.MinValue);
        int isoYear = ISOWeek.GetYear(dt);
        int isoWeek = ISOWeek.GetWeekOfYear(dt);
        var mondayDt = ISOWeek.ToDateTime(isoYear, isoWeek, DayOfWeek.Monday);
        var monday = DateOnly.FromDateTime(mondayDt);
        return new CalendarWeek(isoYear, isoWeek, monday);
    }

    /// <summary>
    /// Creates an ISO 8601 <see cref="CalendarWeek"/> from an ISO week-year and ISO week number (1..52/53).
    /// </summary>
    public static CalendarWeek FromIsoWeek(int isoYear, int isoWeek)
    {
        int weeksInYear = ISOWeek.GetWeeksInYear(isoYear);
        if (isoWeek < 1 || isoWeek > weeksInYear)
        {
            throw new ArgumentOutOfRangeException(
                nameof(isoWeek),
                $"Vecka {isoWeek} är inte giltig för ISO-år {isoYear} (1–{weeksInYear}).");
        }

        var mondayDt = ISOWeek.ToDateTime(isoYear, isoWeek, DayOfWeek.Monday);
        var monday = DateOnly.FromDateTime(mondayDt);
        return new CalendarWeek(isoYear, isoWeek, monday);
    }

    public CalendarWeek PreviousWeek() => FromDate(WeekStartDate.AddDays(-7));

    public CalendarWeek NextWeek() => FromDate(WeekStartDate.AddDays(7));

    /// <summary>
    /// Returns the 7 dates in the week starting from Monday (index 0) to Sunday (index 6).
    /// </summary>
    public IReadOnlyList<DateOnly> GetDates()
    {
        var dates = new DateOnly[7];
        for (int i = 0; i < 7; i++)
        {
            dates[i] = WeekStartDate.AddDays(i);
        }
        return dates;
    }

    public DateOnly GetDateForDayIndex(int dayIndex)
    {
        if (dayIndex < 0 || dayIndex > 6)
            throw new ArgumentOutOfRangeException(nameof(dayIndex), "DayIndex måste vara mellan 0 (Måndag) och 6 (Söndag).");

        return WeekStartDate.AddDays(dayIndex);
    }

    /// <summary>
    /// Returns 0 for Monday .. 6 for Sunday.
    /// </summary>
    public static int GetDayIndex(DateOnly date) =>
        ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;

    public static string GetSwedishDayName(int dayIndex)
    {
        if (dayIndex < 0 || dayIndex > 6)
            throw new ArgumentOutOfRangeException(nameof(dayIndex));

        return SwedishDayNames[dayIndex];
    }

    public static string GetSwedishDayName(DateOnly date) =>
        GetSwedishDayName(GetDayIndex(date));

    public static string FormatDayHeaderSwedish(DateOnly date)
    {
        var dayName = GetSwedishDayName(date).ToUpper(SwedishCulture);
        var monthName = date.ToString("MMMM", SwedishCulture).ToUpper(SwedishCulture);
        return $"{dayName} {date.Day} {monthName}";
    }

    public string FormatDateRangeSwedish()
    {
        var start = WeekStartDate;
        var end = WeekEndDate;

        var startMonth = start.ToString("MMMM", SwedishCulture);
        var endMonth = end.ToString("MMMM", SwedishCulture);

        if (start.Year != end.Year)
        {
            return $"{start.Day} {startMonth} {start.Year} – {end.Day} {endMonth} {end.Year}";
        }

        if (start.Month != end.Month)
        {
            return $"{start.Day} {startMonth} – {end.Day} {endMonth} {end.Year}";
        }

        return $"{start.Day}–{end.Day} {endMonth} {end.Year}";
    }

    /// <summary>
    /// Returns the number of calendar weeks between two week start dates (later - earlier).
    /// </summary>
    public static int WeeksBetween(DateOnly earlierWeekStart, DateOnly laterWeekStart)
    {
        var earlierMonday = FromDate(earlierWeekStart).WeekStartDate;
        var laterMonday = FromDate(laterWeekStart).WeekStartDate;
        return (laterMonday.DayNumber - earlierMonday.DayNumber) / 7;
    }

    /// <summary>
    /// Returns the default category name for the given day index (0 = Måndag .. 6 = Söndag) per specification C1.
    /// </summary>
    public static string GetDefaultCategoryName(int dayIndex) =>
        dayIndex is 4 or 5 ? "Fest & Helg" : "Vardagsmat";
}

