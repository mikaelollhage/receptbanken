namespace Receptbanken.Core.Models;

/// <summary>
/// Stores a single-occurrence cancellation or field override for a recurring <see cref="CalendarEvent"/>.
/// </summary>
public class CalendarEventException
{
    public int Id { get; set; }

    public int CalendarEventId { get; set; }
    public CalendarEvent? CalendarEvent { get; set; }

    /// <summary>
    /// The original occurrence date in the recurrence pattern being cancelled or overridden.
    /// </summary>
    public DateOnly OriginalDate { get; set; }

    /// <summary>
    /// True when this single occurrence was deleted from the series.
    /// </summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Set when the single occurrence was moved to a different date.
    /// </summary>
    public DateOnly? OverrideDate { get; set; }

    public string? OverrideTitle { get; set; }
    public bool? OverrideIsAllDay { get; set; }
    public TimeOnly? OverrideStartTime { get; set; }
    public TimeOnly? OverrideEndTime { get; set; }
    public string? OverrideLocation { get; set; }
    public string? OverrideNotes { get; set; }
    public string? OverrideFamilyMembersCsv { get; set; }
    public int? OverrideReminderMinutesBefore { get; set; }

    /// <summary>
    /// True when the reminder was explicitly disabled for this single occurrence,
    /// overriding a non-null reminder on the parent series.
    /// </summary>
    public bool OverrideReminderCleared { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
