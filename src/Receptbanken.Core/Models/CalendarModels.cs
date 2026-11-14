namespace Receptbanken.Core.Models;

/// <summary>
/// Represents an expanded calendar occurrence for a specific display date, merging series defaults
/// with any <see cref="CalendarEventException"/> overrides.
/// </summary>
public sealed record CalendarOccurrenceDto
{
    public int CalendarEventId { get; init; }
    public DateOnly Date { get; init; }
    public DateOnly OriginalDate { get; init; }
    public bool IsRecurring { get; init; }
    public bool IsException { get; init; }
    public bool IsMoved { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool IsAllDay { get; init; }
    public TimeOnly? StartTime { get; init; }
    public TimeOnly? EndTime { get; init; }
    public string? Location { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> FamilyMembers { get; init; } = [];
    public int? ReminderMinutesBefore { get; init; }
    public int RecurrenceIntervalWeeks { get; init; }
    public IsoWeekdayMask RecurrenceDaysOfWeekMask { get; init; }
    public DateOnly? RecurrenceEndDate { get; init; }
}

/// <summary>
/// Input model for creating or updating a one-time event or an entire recurring series.
/// </summary>
public sealed class CalendarEventInput
{
    public string Title { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public bool IsAllDay { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public IReadOnlyList<string>? FamilyMembers { get; set; }
    public int? ReminderMinutesBefore { get; set; }
    public int RecurrenceIntervalWeeks { get; set; }
    public IsoWeekdayMask RecurrenceDaysOfWeekMask { get; set; }
    public DateOnly? RecurrenceEndDate { get; set; }
}

/// <summary>
/// Input model for editing a single occurrence of a recurring series.
/// </summary>
public sealed class CalendarOccurrenceOverrideInput
{
    public string Title { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public bool IsAllDay { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public IReadOnlyList<string>? FamilyMembers { get; set; }
    public int? ReminderMinutesBefore { get; set; }
}

/// <summary>
/// Normalizes family member names for storage and display without user accounts.
/// </summary>
public static class CalendarFamilyMemberHelper
{
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? members)
    {
        if (members == null)
            return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in members)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // Split on commas too in case a caller passes a comma-containing string.
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }
        }

        return result;
    }

    public static string? FormatCsv(IEnumerable<string>? members)
    {
        var normalized = Normalize(members);
        return normalized.Count == 0 ? null : string.Join(",", normalized);
    }

    public static IReadOnlyList<string> ParseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return [];

        return Normalize([csv]);
    }
}
