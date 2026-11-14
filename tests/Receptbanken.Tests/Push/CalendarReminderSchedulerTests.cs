using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Push;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;

namespace Receptbanken.Tests.Push;

public class CalendarReminderSchedulerTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly FakeTimeProvider _timeProvider;
    private readonly CalendarService _calendarService;

    public CalendarReminderSchedulerTests()
    {
        _dbFactory = new TestDbContextFactory();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        _calendarService = new CalendarService(
            _dbFactory,
            new CalendarNotifier(),
            NullLogger<CalendarService>.Instance,
            _timeProvider);
    }

    public void Dispose() => _dbFactory.Dispose();

    [Fact]
    public void SwedishLocalDateProvider_ConvertSwedishLocalToUtc_ShouldHandleWinterSummerInvalidAndAmbiguousTimes()
    {
        var tz = SwedishLocalDateProvider.GetSwedishTimeZone();

        // Winter time (CET, UTC+1): 2026-01-15 10:00 -> 09:00Z
        var winterUtc = SwedishLocalDateProvider.ConvertSwedishLocalToUtc(
            new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Unspecified), tz);
        winterUtc.Should().Be(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero));

        // Summer time (CEST, UTC+2): 2026-07-15 10:00 -> 08:00Z
        var summerUtc = SwedishLocalDateProvider.ConvertSwedishLocalToUtc(
            new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Unspecified), tz);
        summerUtc.Should().Be(new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero));

        // Spring DST invalid time: 2026-03-29 02:30 (clocks jump 02:00 -> 03:00 CEST) -> 03:00 CEST = 01:00Z
        var springInvalidUtc = SwedishLocalDateProvider.ConvertSwedishLocalToUtc(
            new DateTime(2026, 3, 29, 2, 30, 0, DateTimeKind.Unspecified), tz);
        springInvalidUtc.Should().Be(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero));

        // Autumn DST ambiguous time: 2026-10-25 02:30 -> first occurrence (CEST, UTC+2) = 00:30Z
        var autumnAmbiguousUtc = SwedishLocalDateProvider.ConvertSwedishLocalToUtc(
            new DateTime(2026, 10, 25, 2, 30, 0, DateTimeKind.Unspecified), tz);
        autumnAmbiguousUtc.Should().Be(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Scheduler_ShouldSendToAllActiveDevicesOnce_AndNeverResendSentDeliveries()
    {
        // 2026-11-14 10:00 Stockholm (UTC+1) = 09:00Z
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 9, 1, 0, TimeSpan.Zero));

        var sub1 = await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/dev-1", true);
        var sub2 = await AddSubscriptionAsync("https://updates.push.services.mozilla.com/wpush/v2/dev-2", true);
        await AddSubscriptionAsync("https://web.push.apple.com/inactive-dev", false);

        await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Lördagsfika",
            StartDate = new DateOnly(2026, 11, 14),
            IsAllDay = false,
            StartTime = new TimeOnly(10, 0),
            ReminderMinutesBefore = CalendarReminderOptions.AtStart
        });

        var client = new RecordingWebPushClient((_, _) => new WebPushSendResult(true, 201, null, false, false));
        var scheduler = CreateScheduler(client);

        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(2);
        client.SentPayloads.Select(x => x.SubscriptionId).Should().BeEquivalentTo([sub1.Id, sub2.Id]);

        // Second run within 15-minute window must not resend
        _timeProvider.Advance(TimeSpan.FromMinutes(2));
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(2);

        using var db = _dbFactory.CreateDbContext();
        var deliveries = await db.CalendarReminderDeliveries.ToListAsync();
        deliveries.Should().HaveCount(2);
        deliveries.Should().OnlyContain(d => d.Status == CalendarReminderDeliveryStatus.Sent && d.SentAtUtc != null);
    }

    [Fact]
    public async Task Scheduler_ShouldSupportMidnightCrossingsAndBothAllDayReminders()
    {
        // Event is tomorrow 2026-11-15 all-day with 18:00 previous evening reminder (2026-11-14 18:00 Stockholm = 17:00Z)
        // Also an event today 2026-11-14 at 00:05 Stockholm (2026-11-13 23:05Z) with 15-min reminder (2026-11-13 23:50 Stockholm = 22:50Z)
        var sub = await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/midnight-dev", true);

        await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Tidig söndag",
            StartDate = new DateOnly(2026, 11, 15),
            IsAllDay = false,
            StartTime = new TimeOnly(0, 5),
            ReminderMinutesBefore = CalendarReminderOptions.Minutes15 // 2026-11-14 23:50 Stockholm = 22:50Z
        });

        await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Helgdag imorgon",
            StartDate = new DateOnly(2026, 11, 15),
            IsAllDay = true,
            ReminderMinutesBefore = CalendarReminderOptions.AllDayPreviousEvening1800 // 2026-11-14 18:00 Stockholm = 17:00Z
        });

        await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Helgdag idag",
            StartDate = new DateOnly(2026, 11, 14),
            IsAllDay = true,
            ReminderMinutesBefore = CalendarReminderOptions.AllDaySameDay0900 // 2026-11-14 09:00 Stockholm = 08:00Z
        });

        var client = new RecordingWebPushClient((_, _) => new WebPushSendResult(true, 201, null, false, false));
        var scheduler = CreateScheduler(client);

        // 09:02 Stockholm (08:02Z) -> "Helgdag idag" 09:00 fires
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 8, 2, 0, TimeSpan.Zero));
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(1);
        JsonDocument.Parse(client.SentPayloads[0].PayloadJson).RootElement.GetProperty("title").GetString().Should().Be("Helgdag idag");

        // 18:02 Stockholm (17:02Z) -> "Helgdag imorgon" 18:00 fires
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 17, 2, 0, TimeSpan.Zero));
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(2);
        JsonDocument.Parse(client.SentPayloads[1].PayloadJson).RootElement.GetProperty("title").GetString().Should().Be("Helgdag imorgon");

        // 23:52 Stockholm on 2026-11-14 (22:52Z) -> "Tidig söndag" (2026-11-15 00:05) fires
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 22, 52, 0, TimeSpan.Zero));
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(3);
        JsonDocument.Parse(client.SentPayloads[2].PayloadJson).RootElement.GetProperty("title").GetString().Should().Be("Tidig söndag");
    }

    [Fact]
    public async Task Scheduler_ShouldRecoverPendingAfterCrash_AndReclaimExpiredInProgressLease()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 9, 1, 0, TimeSpan.Zero));
        var sub = await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/crash-dev", true);

        var ev = await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Återhämtning",
            StartDate = new DateOnly(2026, 11, 14),
            IsAllDay = false,
            StartTime = new TimeOnly(10, 0),
            ReminderMinutesBefore = CalendarReminderOptions.AtStart
        });

        // Simulate crash after creating Pending row & acquiring an expired InProgress lease
        using (var db = _dbFactory.CreateDbContext())
        {
            db.CalendarReminderDeliveries.Add(new CalendarReminderDelivery
            {
                CalendarEventId = ev.Id,
                OccurrenceOriginalDate = new DateOnly(2026, 11, 14),
                OccurrenceDisplayDate = new DateOnly(2026, 11, 14),
                ScheduledTriggerUtc = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
                PushSubscriptionId = sub.Id,
                Status = CalendarReminderDeliveryStatus.InProgress,
                LeaseToken = "crashed-worker-token",
                LockedUntilUtc = _timeProvider.GetUtcNow().AddSeconds(-10), // expired lease
                NextAttemptAtUtc = _timeProvider.GetUtcNow().AddMinutes(-3),
                ExpiresAtUtc = new DateTimeOffset(2026, 11, 14, 9, 15, 0, TimeSpan.Zero),
                CreatedAt = _timeProvider.GetUtcNow().AddMinutes(-3),
                UpdatedAt = _timeProvider.GetUtcNow().AddMinutes(-3)
            });
            await db.SaveChangesAsync();
        }

        var client = new RecordingWebPushClient((_, _) => new WebPushSendResult(true, 201, null, false, false));
        var scheduler = CreateScheduler(client);

        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(1);

        using (var db = _dbFactory.CreateDbContext())
        {
            var delivery = await db.CalendarReminderDeliveries.SingleAsync();
            delivery.Status.Should().Be(CalendarReminderDeliveryStatus.Sent);
            delivery.LeaseToken.Should().BeNull();
        }
    }

    [Fact]
    public async Task Scheduler_ShouldRetryTransientErrorsUpToMax4Attempts_WithBackoff()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 9, 0, 30, TimeSpan.Zero));
        await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/retry-dev", true);

        await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Retry-test",
            StartDate = new DateOnly(2026, 11, 14),
            IsAllDay = false,
            StartTime = new TimeOnly(10, 0),
            ReminderMinutesBefore = CalendarReminderOptions.AtStart
        });

        var client = new RecordingWebPushClient((_, _) =>
            new WebPushSendResult(false, 503, "HTTP 503", false, IsTransientFailure: true));
        var scheduler = CreateScheduler(client);

        // Attempt 1 (at +30s) -> Pending with +30s backoff
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(1);

        // Before 30s passes -> should not retry yet
        _timeProvider.Advance(TimeSpan.FromSeconds(15));
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(1);

        // Attempt 2 (after +30s) -> Pending with +120s backoff
        _timeProvider.Advance(TimeSpan.FromSeconds(20));
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(2);

        // Attempt 3 (after +120s) -> Pending with +300s backoff
        _timeProvider.Advance(TimeSpan.FromSeconds(125));
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(3);

        // Attempt 4 (after +300s) -> FailedPermanent (max 4 attempts reached)
        _timeProvider.Advance(TimeSpan.FromSeconds(305));
        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(4);

        using var db = _dbFactory.CreateDbContext();
        var delivery = await db.CalendarReminderDeliveries.SingleAsync();
        delivery.Status.Should().Be(CalendarReminderDeliveryStatus.FailedPermanent);
        delivery.AttemptCount.Should().Be(4);
    }

    [Fact]
    public async Task Scheduler_ShouldDeactivateSubscriptionOn410Gone()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 9, 1, 0, TimeSpan.Zero));
        var sub = await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/gone-dev", true);

        await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Gone-test",
            StartDate = new DateOnly(2026, 11, 14),
            IsAllDay = false,
            StartTime = new TimeOnly(10, 0),
            ReminderMinutesBefore = CalendarReminderOptions.AtStart
        });

        var client = new RecordingWebPushClient((_, _) =>
            new WebPushSendResult(false, 410, "HTTP 410", ShouldDeactivateSubscription: true, IsTransientFailure: false));
        var scheduler = CreateScheduler(client);

        await scheduler.RunOnceAsync();

        using var db = _dbFactory.CreateDbContext();
        var reloadedSub = await db.PushSubscriptions.SingleAsync(s => s.Id == sub.Id);
        reloadedSub.IsActive.Should().BeFalse();

        var delivery = await db.CalendarReminderDeliveries.SingleAsync();
        delivery.Status.Should().Be(CalendarReminderDeliveryStatus.FailedPermanent);
        delivery.LastHttpStatusCode.Should().Be(410);
    }

    [Fact]
    public async Task Scheduler_ShouldCleanupDeliveriesOlderThan14Days()
    {
        var now = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(now);
        var sub = await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/cleanup-dev", true);
        var ev = await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Gammal händelse",
            StartDate = new DateOnly(2026, 10, 1),
            IsAllDay = true,
            ReminderMinutesBefore = null
        });

        using (var db = _dbFactory.CreateDbContext())
        {
            db.CalendarReminderDeliveries.AddRange(
                new CalendarReminderDelivery
                {
                    CalendarEventId = ev.Id,
                    OccurrenceOriginalDate = new DateOnly(2026, 10, 1),
                    OccurrenceDisplayDate = new DateOnly(2026, 10, 1),
                    ScheduledTriggerUtc = now.AddDays(-15),
                    PushSubscriptionId = sub.Id,
                    Status = CalendarReminderDeliveryStatus.Sent,
                    ExpiresAtUtc = now.AddDays(-15).AddMinutes(15),
                    CreatedAt = now.AddDays(-15),
                    UpdatedAt = now.AddDays(-15)
                },
                new CalendarReminderDelivery
                {
                    CalendarEventId = ev.Id,
                    OccurrenceOriginalDate = new DateOnly(2026, 11, 10),
                    OccurrenceDisplayDate = new DateOnly(2026, 11, 10),
                    ScheduledTriggerUtc = now.AddDays(-4),
                    PushSubscriptionId = sub.Id,
                    Status = CalendarReminderDeliveryStatus.Sent,
                    ExpiresAtUtc = now.AddDays(-4).AddMinutes(15),
                    CreatedAt = now.AddDays(-4),
                    UpdatedAt = now.AddDays(-4)
                });
            await db.SaveChangesAsync();
        }

        var scheduler = CreateScheduler(new RecordingWebPushClient((_, _) => new WebPushSendResult(true, 201, null, false, false)));
        await scheduler.RunOnceAsync();

        using (var db = _dbFactory.CreateDbContext())
        {
            var remaining = await db.CalendarReminderDeliveries.ToListAsync();
            remaining.Should().HaveCount(1);
            remaining[0].OccurrenceOriginalDate.Should().Be(new DateOnly(2026, 11, 10));
        }
    }

    [Fact]
    public async Task CalendarService_UpdateAndDelete_ShouldCancelStaleDeliveries_AndAllowNewSchedule()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 9, 1, 0, TimeSpan.Zero));
        var sub = await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/update-dev", true);

        var series = await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Träning",
            StartDate = new DateOnly(2026, 11, 14),
            IsAllDay = false,
            StartTime = new TimeOnly(10, 0),
            ReminderMinutesBefore = CalendarReminderOptions.AtStart,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Saturday
        });

        // Seed a Pending delivery for 2026-11-14 10:00 (09:00Z)
        using (var db = _dbFactory.CreateDbContext())
        {
            db.CalendarReminderDeliveries.Add(new CalendarReminderDelivery
            {
                CalendarEventId = series.Id,
                OccurrenceOriginalDate = new DateOnly(2026, 11, 14),
                OccurrenceDisplayDate = new DateOnly(2026, 11, 14),
                ScheduledTriggerUtc = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
                PushSubscriptionId = sub.Id,
                Status = CalendarReminderDeliveryStatus.Pending,
                NextAttemptAtUtc = _timeProvider.GetUtcNow(),
                ExpiresAtUtc = new DateTimeOffset(2026, 11, 14, 9, 15, 0, TimeSpan.Zero),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }

        // Move single occurrence from 10:00 to 10:15 with AtStart reminder
        await _calendarService.UpdateSingleOccurrenceAsync(series.Id, new DateOnly(2026, 11, 14), new CalendarOccurrenceOverrideInput
        {
            Title = "Träning flyttad",
            Date = new DateOnly(2026, 11, 14),
            IsAllDay = false,
            StartTime = new TimeOnly(10, 15),
            ReminderMinutesBefore = CalendarReminderOptions.AtStart
        });

        using (var db = _dbFactory.CreateDbContext())
        {
            var oldDelivery = await db.CalendarReminderDeliveries.SingleAsync();
            oldDelivery.Status.Should().Be(CalendarReminderDeliveryStatus.Cancelled);
        }

        // Advance to 10:16 Stockholm (09:16Z) -> new reminder fires
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 9, 16, 0, TimeSpan.Zero));
        var client = new RecordingWebPushClient((_, _) => new WebPushSendResult(true, 201, null, false, false));
        var scheduler = CreateScheduler(client);

        await scheduler.RunOnceAsync();
        client.SentPayloads.Should().HaveCount(1);
        JsonDocument.Parse(client.SentPayloads[0].PayloadJson).RootElement.GetProperty("title").GetString().Should().Be("Träning flyttad");
    }

    [Fact]
    public async Task Scheduler_PreSendVerification_ShouldCancelWithoutHttpIfEventWasDeletedBeforeSend()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 14, 9, 1, 0, TimeSpan.Zero));
        var sub = await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/presend-dev", true);

        var ev = await _calendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Avbokas",
            StartDate = new DateOnly(2026, 11, 14),
            IsAllDay = false,
            StartTime = new TimeOnly(10, 0),
            ReminderMinutesBefore = CalendarReminderOptions.AtStart,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Saturday
        });

        // Cancel single occurrence directly in DB (bypassing CalendarService) to test worker's pre-send check!
        using (var db = _dbFactory.CreateDbContext())
        {
            db.CalendarReminderDeliveries.Add(new CalendarReminderDelivery
            {
                CalendarEventId = ev.Id,
                OccurrenceOriginalDate = new DateOnly(2026, 11, 14),
                OccurrenceDisplayDate = new DateOnly(2026, 11, 14),
                ScheduledTriggerUtc = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
                PushSubscriptionId = sub.Id,
                Status = CalendarReminderDeliveryStatus.Pending,
                NextAttemptAtUtc = _timeProvider.GetUtcNow(),
                ExpiresAtUtc = new DateTimeOffset(2026, 11, 14, 9, 15, 0, TimeSpan.Zero),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow()
            });
            db.CalendarEventExceptions.Add(new CalendarEventException
            {
                CalendarEventId = ev.Id,
                OriginalDate = new DateOnly(2026, 11, 14),
                IsCancelled = true
            });
            await db.SaveChangesAsync();
        }

        var client = new RecordingWebPushClient((_, _) => new WebPushSendResult(true, 201, null, false, false));
        var scheduler = CreateScheduler(client);

        await scheduler.RunOnceAsync();

        client.SentPayloads.Should().BeEmpty();
        using (var db = _dbFactory.CreateDbContext())
        {
            var delivery = await db.CalendarReminderDeliveries.SingleAsync();
            delivery.Status.Should().Be(CalendarReminderDeliveryStatus.Cancelled);
        }
    }

    private CalendarReminderScheduler CreateScheduler(IWebPushClient client) =>
        new(_dbFactory, client, NullLogger<CalendarReminderScheduler>.Instance, _timeProvider);

    private async Task<PushSubscriptionEntity> AddSubscriptionAsync(string endpoint, bool isActive)
    {
        using var db = _dbFactory.CreateDbContext();
        var sub = new PushSubscriptionEntity
        {
            Endpoint = endpoint,
            P256dh = "BTestKey",
            Auth = "TestAuth",
            IsActive = isActive,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow()
        };
        db.PushSubscriptions.Add(sub);
        await db.SaveChangesAsync();
        return sub;
    }

    private sealed class RecordingWebPushClient(Func<PushSubscriptionEntity, string, WebPushSendResult> responder) : IWebPushClient
    {
        public List<(int SubscriptionId, string PayloadJson)> SentPayloads { get; } = [];

        public Task<WebPushSendResult> SendAsync(
            PushSubscriptionEntity subscription,
            string payloadJson,
            int ttlSeconds = 900,
            CancellationToken cancellationToken = default)
        {
            lock (SentPayloads)
            {
                SentPayloads.Add((subscription.Id, payloadJson));
            }
            return Task.FromResult(responder(subscription, payloadJson));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
