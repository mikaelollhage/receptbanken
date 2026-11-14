namespace Receptbanken.Core.Models;

public enum CalendarReminderDeliveryStatus
{
    Pending = 0,
    InProgress = 1,
    Sent = 2,
    FailedPermanent = 3,
    Cancelled = 4,
    Expired = 5
}

/// <summary>
/// Persistent delivery queue item for a single calendar occurrence reminder and push subscription.
/// </summary>
public class CalendarReminderDelivery
{
    public const int MaxLeaseTokenLength = 64;
    public const int MaxLastErrorLength = 500;

    public long Id { get; set; }

    public int CalendarEventId { get; set; }
    public CalendarEvent? CalendarEvent { get; set; }

    public DateOnly OccurrenceOriginalDate { get; set; }
    public DateOnly OccurrenceDisplayDate { get; set; }
    public DateTimeOffset ScheduledTriggerUtc { get; set; }

    public int PushSubscriptionId { get; set; }
    public PushSubscriptionEntity? PushSubscription { get; set; }

    public CalendarReminderDeliveryStatus Status { get; set; } = CalendarReminderDeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public int? LastHttpStatusCode { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
