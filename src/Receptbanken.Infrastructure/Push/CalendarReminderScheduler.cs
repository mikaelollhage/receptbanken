using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Push;

/// <summary>
/// Schedules due calendar reminders into SQLite, claims deliveries with atomic leases,
/// verifies live calendar state before HTTP dispatch, and handles retries/cleanup.
/// </summary>
public sealed class CalendarReminderScheduler
{
    public static readonly TimeSpan ValidityWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);
    public const int MaxAttempts = 4;
    public const int MaxConcurrentSends = 4;

    private static readonly int[] RetryBackoffSeconds = [30, 120, 300];

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IWebPushClient _webPushClient;
    private readonly ILogger<CalendarReminderScheduler> _logger;
    private readonly TimeProvider _timeProvider;

    public CalendarReminderScheduler(
        IDbContextFactory<AppDbContext> dbFactory,
        IWebPushClient webPushClient,
        ILogger<CalendarReminderScheduler> logger,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _webPushClient = webPushClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Executes a single scheduling, claiming, and delivery cycle.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = _timeProvider.GetUtcNow();

        await CleanupOldDeliveriesAsync(utcNow, cancellationToken);
        await EnqueueDueDeliveriesAsync(utcNow, cancellationToken);
        await ExpireStaleDeliveriesAsync(utcNow, cancellationToken);

        var claimed = await ClaimDueDeliveriesAsync(utcNow, cancellationToken);
        if (claimed.Count == 0)
            return;

        using var throttle = new SemaphoreSlim(MaxConcurrentSends, MaxConcurrentSends);
        var tasks = claimed.Select(item => ProcessClaimedDeliveryAsync(item.DeliveryId, item.LeaseToken, throttle, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task CleanupOldDeliveriesAsync(DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        var cutoff = utcNow - RetentionPeriod;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.CalendarReminderDeliveries
            .Where(d => d.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Rensade {Count} gamla leveransrader äldre än 14 dagar.", deleted);
        }
    }

    private async Task EnqueueDueDeliveriesAsync(DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var activeSubscriptions = await db.PushSubscriptions
            .AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        if (activeSubscriptions.Count == 0)
            return;

        var systemState = await db.CalendarReminderSystemStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == CalendarReminderSystemState.SingletonId, cancellationToken);

        var windowFloor = utcNow - ValidityWindow;
        var effectiveWindowStartUtc = systemState?.LastContentRestoredAtUtc is { } restoredAt && restoredAt > windowFloor
            ? restoredAt
            : windowFloor;

        var stockholmToday = SwedishLocalDateProvider.GetSwedishToday(_timeProvider);
        var rangeStart = stockholmToday.AddDays(-1);
        var rangeEnd = stockholmToday.AddDays(2);

        var events = await db.CalendarEvents
            .AsNoTracking()
            .Include(e => e.Exceptions)
            .Where(e =>
                (e.StartDate <= rangeEnd && (e.RecurrenceEndDate == null || e.RecurrenceEndDate >= rangeStart)) ||
                e.Exceptions.Any(ex =>
                    !ex.IsCancelled &&
                    ex.OverrideDate != null &&
                    ex.OverrideDate >= rangeStart &&
                    ex.OverrideDate <= rangeEnd))
            .ToListAsync(cancellationToken);

        if (events.Count == 0)
            return;

        var occurrences = CalendarRecurrenceExpander.Expand(events, rangeStart, rangeEnd);
        var swedishTz = SwedishLocalDateProvider.GetSwedishTimeZone();

        var dueReminders = new List<(CalendarOccurrenceDto Occurrence, DateTimeOffset TriggerUtc, DateTimeOffset ExpiresAtUtc)>();
        foreach (var occ in occurrences)
        {
            if (!occ.ReminderMinutesBefore.HasValue)
                continue;

            var localReminder = CalendarReminderOptions.GetReminderLocalDateTime(
                occ.Date,
                occ.IsAllDay,
                occ.StartTime,
                occ.ReminderMinutesBefore);

            if (!localReminder.HasValue)
                continue;

            var triggerUtc = SwedishLocalDateProvider.ConvertSwedishLocalToUtc(localReminder.Value, swedishTz);
            var expiresAtUtc = triggerUtc + ValidityWindow;

            if (triggerUtc <= utcNow &&
                expiresAtUtc > utcNow &&
                triggerUtc >= effectiveWindowStartUtc)
            {
                dueReminders.Add((occ, triggerUtc, expiresAtUtc));
            }
        }

        if (dueReminders.Count == 0)
            return;

        var eventIds = dueReminders.Select(r => r.Occurrence.CalendarEventId).Distinct().ToList();
        var existingKeys = (await db.CalendarReminderDeliveries
            .AsNoTracking()
            .Where(d => eventIds.Contains(d.CalendarEventId) && d.ExpiresAtUtc > windowFloor)
            .Select(d => new
            {
                d.CalendarEventId,
                d.OccurrenceOriginalDate,
                d.ScheduledTriggerUtc,
                d.PushSubscriptionId
            })
            .ToListAsync(cancellationToken))
            .Select(k => (k.CalendarEventId, k.OccurrenceOriginalDate, k.ScheduledTriggerUtc, k.PushSubscriptionId))
            .ToHashSet();

        var newDeliveries = new List<CalendarReminderDelivery>();
        foreach (var reminder in dueReminders)
        {
            foreach (var subId in activeSubscriptions)
            {
                var key = (
                    reminder.Occurrence.CalendarEventId,
                    reminder.Occurrence.OriginalDate,
                    reminder.TriggerUtc,
                    subId);

                if (existingKeys.Add(key))
                {
                    newDeliveries.Add(new CalendarReminderDelivery
                    {
                        CalendarEventId = reminder.Occurrence.CalendarEventId,
                        OccurrenceOriginalDate = reminder.Occurrence.OriginalDate,
                        OccurrenceDisplayDate = reminder.Occurrence.Date,
                        ScheduledTriggerUtc = reminder.TriggerUtc,
                        PushSubscriptionId = subId,
                        Status = CalendarReminderDeliveryStatus.Pending,
                        AttemptCount = 0,
                        NextAttemptAtUtc = utcNow,
                        ExpiresAtUtc = reminder.ExpiresAtUtc,
                        CreatedAt = utcNow,
                        UpdatedAt = utcNow
                    });
                }
            }
        }

        if (newDeliveries.Count == 0)
            return;

        try
        {
            db.CalendarReminderDeliveries.AddRange(newDeliveries);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Fallback to individual inserts if a concurrent worker inserted one of the keys.
            foreach (var item in newDeliveries)
            {
                await using var singleDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
                singleDb.CalendarReminderDeliveries.Add(item);
                try
                {
                    await singleDb.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Already inserted by another worker.
                }
            }
        }
    }

    private async Task ExpireStaleDeliveriesAsync(DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await db.CalendarReminderDeliveries
            .Where(d =>
                (d.Status == CalendarReminderDeliveryStatus.Pending ||
                 (d.Status == CalendarReminderDeliveryStatus.InProgress && d.LockedUntilUtc != null && d.LockedUntilUtc <= utcNow)) &&
                d.ExpiresAtUtc <= utcNow)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.Status, CalendarReminderDeliveryStatus.Expired)
                .SetProperty(d => d.LeaseToken, (string?)null)
                .SetProperty(d => d.LockedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(d => d.UpdatedAt, utcNow),
                cancellationToken);
    }

    private async Task<List<(long DeliveryId, string LeaseToken)>> ClaimDueDeliveriesAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var candidateIds = await db.CalendarReminderDeliveries
            .AsNoTracking()
            .Where(d =>
                d.ExpiresAtUtc > utcNow &&
                ((d.Status == CalendarReminderDeliveryStatus.Pending && d.NextAttemptAtUtc <= utcNow) ||
                 (d.Status == CalendarReminderDeliveryStatus.InProgress && d.LockedUntilUtc != null && d.LockedUntilUtc <= utcNow)))
            .OrderBy(d => d.NextAttemptAtUtc)
            .Select(d => d.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        var claimed = new List<(long DeliveryId, string LeaseToken)>();
        var leaseExpiry = utcNow + LeaseDuration;

        foreach (var id in candidateIds)
        {
            var leaseToken = Guid.NewGuid().ToString("N");
            var affected = await db.CalendarReminderDeliveries
                .Where(d =>
                    d.Id == id &&
                    d.ExpiresAtUtc > utcNow &&
                    ((d.Status == CalendarReminderDeliveryStatus.Pending && d.NextAttemptAtUtc <= utcNow) ||
                     (d.Status == CalendarReminderDeliveryStatus.InProgress && d.LockedUntilUtc != null && d.LockedUntilUtc <= utcNow)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.Status, CalendarReminderDeliveryStatus.InProgress)
                    .SetProperty(d => d.LeaseToken, leaseToken)
                    .SetProperty(d => d.LockedUntilUtc, leaseExpiry)
                    .SetProperty(d => d.UpdatedAt, utcNow),
                    cancellationToken);

            if (affected == 1)
            {
                claimed.Add((id, leaseToken));
            }
        }

        return claimed;
    }

    private async Task ProcessClaimedDeliveryAsync(
        long deliveryId,
        string leaseToken,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            var preSend = await VerifyBeforeSendAsync(deliveryId, leaseToken, cancellationToken);
            if (preSend == null)
                return;

            var ttlSeconds = Math.Max(60, (int)Math.Ceiling((preSend.Delivery.ExpiresAtUtc - _timeProvider.GetUtcNow()).TotalSeconds));
            var result = await _webPushClient.SendAsync(
                preSend.Subscription,
                preSend.PayloadJson,
                ttlSeconds,
                cancellationToken);

            await RecordSendResultAsync(preSend.Delivery, leaseToken, result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fel vid bearbetning av kalenderleverans {DeliveryId}.", deliveryId);
        }
        finally
        {
            throttle.Release();
        }
    }

    private async Task<PreSendContext?> VerifyBeforeSendAsync(
        long deliveryId,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var delivery = await db.CalendarReminderDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);

        if (delivery == null ||
            delivery.Status != CalendarReminderDeliveryStatus.InProgress ||
            !string.Equals(delivery.LeaseToken, leaseToken, StringComparison.Ordinal))
        {
            return null;
        }

        if (delivery.ExpiresAtUtc <= now)
        {
            await MarkStatusWithLeaseAsync(db, deliveryId, leaseToken, CalendarReminderDeliveryStatus.Expired, now, cancellationToken);
            return null;
        }

        var subscription = await db.PushSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == delivery.PushSubscriptionId, cancellationToken);

        if (subscription == null || !subscription.IsActive)
        {
            await MarkStatusWithLeaseAsync(db, deliveryId, leaseToken, CalendarReminderDeliveryStatus.Cancelled, now, cancellationToken);
            return null;
        }

        var calendarEvent = await db.CalendarEvents
            .AsNoTracking()
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.Id == delivery.CalendarEventId, cancellationToken);

        if (calendarEvent == null)
        {
            await MarkStatusWithLeaseAsync(db, deliveryId, leaseToken, CalendarReminderDeliveryStatus.Cancelled, now, cancellationToken);
            return null;
        }

        var expanded = CalendarRecurrenceExpander.ExpandEvent(
            calendarEvent,
            delivery.OccurrenceDisplayDate,
            delivery.OccurrenceDisplayDate);

        var matchingOccurrence = expanded.FirstOrDefault(o =>
            o.OriginalDate == delivery.OccurrenceOriginalDate &&
            o.Date == delivery.OccurrenceDisplayDate);

        if (matchingOccurrence == null || !matchingOccurrence.ReminderMinutesBefore.HasValue)
        {
            await MarkStatusWithLeaseAsync(db, deliveryId, leaseToken, CalendarReminderDeliveryStatus.Cancelled, now, cancellationToken);
            return null;
        }

        var localReminder = CalendarReminderOptions.GetReminderLocalDateTime(
            matchingOccurrence.Date,
            matchingOccurrence.IsAllDay,
            matchingOccurrence.StartTime,
            matchingOccurrence.ReminderMinutesBefore);

        if (!localReminder.HasValue)
        {
            await MarkStatusWithLeaseAsync(db, deliveryId, leaseToken, CalendarReminderDeliveryStatus.Cancelled, now, cancellationToken);
            return null;
        }

        var currentTriggerUtc = SwedishLocalDateProvider.ConvertSwedishLocalToUtc(
            localReminder.Value,
            SwedishLocalDateProvider.GetSwedishTimeZone());

        if (currentTriggerUtc != delivery.ScheduledTriggerUtc)
        {
            await MarkStatusWithLeaseAsync(db, deliveryId, leaseToken, CalendarReminderDeliveryStatus.Cancelled, now, cancellationToken);
            return null;
        }

        var body = BuildNotificationBody(matchingOccurrence);
        var payloadJson = JsonSerializer.Serialize(new
        {
            title = matchingOccurrence.Title,
            body,
            url = $"/kalender?datum={matchingOccurrence.Date:yyyy-MM-dd}",
            tag = $"cal-{matchingOccurrence.CalendarEventId}-{matchingOccurrence.OriginalDate:yyyy-MM-dd}-{delivery.ScheduledTriggerUtc.ToUnixTimeSeconds()}"
        });

        return new PreSendContext(delivery, subscription, payloadJson);
    }

    private async Task RecordSendResultAsync(
        CalendarReminderDelivery deliverySnapshot,
        string leaseToken,
        WebPushSendResult result,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var delivery = await db.CalendarReminderDeliveries
            .FirstOrDefaultAsync(d => d.Id == deliverySnapshot.Id, cancellationToken);

        if (delivery == null ||
            delivery.Status != CalendarReminderDeliveryStatus.InProgress ||
            !string.Equals(delivery.LeaseToken, leaseToken, StringComparison.Ordinal))
        {
            return;
        }

        var subscription = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Id == delivery.PushSubscriptionId, cancellationToken);

        delivery.LeaseToken = null;
        delivery.LockedUntilUtc = null;
        delivery.LastHttpStatusCode = result.StatusCode;
        delivery.LastError = TruncateError(result.ErrorMessage);
        delivery.UpdatedAt = now;

        if (result.IsSuccess)
        {
            delivery.Status = CalendarReminderDeliveryStatus.Sent;
            delivery.SentAtUtc = now;
            delivery.LastError = null;

            if (subscription != null)
            {
                subscription.ConsecutiveFailures = 0;
                subscription.LastSuccessAt = now;
                subscription.UpdatedAt = now;
            }
        }
        else if (result.ShouldDeactivateSubscription)
        {
            delivery.AttemptCount += 1;
            delivery.Status = CalendarReminderDeliveryStatus.FailedPermanent;

            if (subscription != null)
            {
                subscription.IsActive = false;
                subscription.ConsecutiveFailures += 1;
                subscription.LastFailureAt = now;
                subscription.UpdatedAt = now;
            }
        }
        else if (result.IsTransientFailure)
        {
            delivery.AttemptCount += 1;
            var backoffIndex = Math.Clamp(delivery.AttemptCount - 1, 0, RetryBackoffSeconds.Length - 1);
            var nextAttemptAt = now.AddSeconds(RetryBackoffSeconds[backoffIndex]);

            if (delivery.AttemptCount >= MaxAttempts)
            {
                delivery.Status = CalendarReminderDeliveryStatus.FailedPermanent;
            }
            else if (nextAttemptAt >= delivery.ExpiresAtUtc)
            {
                delivery.Status = CalendarReminderDeliveryStatus.Expired;
            }
            else
            {
                delivery.Status = CalendarReminderDeliveryStatus.Pending;
                delivery.NextAttemptAtUtc = nextAttemptAt;
            }

            if (subscription != null)
            {
                subscription.ConsecutiveFailures += 1;
                subscription.LastFailureAt = now;
                subscription.UpdatedAt = now;
            }
        }
        else
        {
            delivery.AttemptCount += 1;
            delivery.Status = CalendarReminderDeliveryStatus.FailedPermanent;

            if (subscription != null)
            {
                subscription.ConsecutiveFailures += 1;
                subscription.LastFailureAt = now;
                subscription.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task MarkStatusWithLeaseAsync(
        AppDbContext db,
        long deliveryId,
        string leaseToken,
        CalendarReminderDeliveryStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await db.CalendarReminderDeliveries
            .Where(d => d.Id == deliveryId && d.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.LeaseToken, (string?)null)
                .SetProperty(d => d.LockedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(d => d.UpdatedAt, now),
                cancellationToken);
    }

    private static string BuildNotificationBody(CalendarOccurrenceDto occurrence)
    {
        var timePart = occurrence.IsAllDay
            ? $"Heldag · {occurrence.Date:yyyy-MM-dd}"
            : $"{occurrence.StartTime:HH\\:mm} · {occurrence.Date:yyyy-MM-dd}";

        return string.IsNullOrWhiteSpace(occurrence.Location)
            ? timePart
            : $"{timePart} · {occurrence.Location}";
    }

    private static string? TruncateError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return null;

        return error.Length <= CalendarReminderDelivery.MaxLastErrorLength
            ? error
            : error[..CalendarReminderDelivery.MaxLastErrorLength];
    }

    private sealed record PreSendContext(
        CalendarReminderDelivery Delivery,
        PushSubscriptionEntity Subscription,
        string PayloadJson);
}
