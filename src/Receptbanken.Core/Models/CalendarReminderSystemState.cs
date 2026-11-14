namespace Receptbanken.Core.Models;

/// <summary>
/// Single-row persistent state for calendar reminder scheduling, including the backup restore barrier.
/// </summary>
public class CalendarReminderSystemState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public DateTimeOffset? LastContentRestoredAtUtc { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
