using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Push;

public sealed record PushSubscriptionInput(
    string Endpoint,
    string P256dh,
    string Auth,
    string? DeviceLabel = null);

public sealed record PushSubscriptionOperationResult(
    bool Success,
    string? ErrorMessage = null,
    int? SubscriptionId = null);

public sealed record PushSubscriptionStatusDto(
    bool IsSubscribed,
    int ActiveDeviceCount,
    int MaxActiveDevices,
    string? VapidPublicKey);

public sealed record PushTestNotificationResult(
    bool Success,
    string Message);

public sealed class PushSubscriptionService
{
    public const int MaxActiveSubscriptions = 25;
    public static readonly TimeSpan TestNotificationCooldown = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IVapidKeyProvider _vapidKeyProvider;
    private readonly IWebPushClient _webPushClient;
    private readonly ILogger<PushSubscriptionService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTestSentByEndpoint = new(StringComparer.Ordinal);

    public PushSubscriptionService(
        IDbContextFactory<AppDbContext> dbFactory,
        IVapidKeyProvider vapidKeyProvider,
        IWebPushClient webPushClient,
        ILogger<PushSubscriptionService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _vapidKeyProvider = vapidKeyProvider;
        _webPushClient = webPushClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetPublicVapidKeyAsync(CancellationToken cancellationToken = default)
    {
        var keys = await _vapidKeyProvider.GetOrCreateKeysAsync(cancellationToken);
        return keys.PublicKeyBase64Url;
    }

    public async Task<PushSubscriptionStatusDto> GetStatusAsync(
        string? currentEndpoint,
        CancellationToken cancellationToken = default)
    {
        var keys = await _vapidKeyProvider.GetOrCreateKeysAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var activeCount = await db.PushSubscriptions.CountAsync(s => s.IsActive, cancellationToken);
        var isSubscribed = false;

        if (!string.IsNullOrWhiteSpace(currentEndpoint))
        {
            var trimmed = currentEndpoint.Trim();
            isSubscribed = await db.PushSubscriptions
                .AnyAsync(s => s.Endpoint == trimmed && s.IsActive, cancellationToken);
        }

        return new PushSubscriptionStatusDto(
            IsSubscribed: isSubscribed,
            ActiveDeviceCount: activeCount,
            MaxActiveDevices: MaxActiveSubscriptions,
            VapidPublicKey: keys.PublicKeyBase64Url);
    }

    public async Task<PushSubscriptionOperationResult> SubscribeOrUpdateAsync(
        PushSubscriptionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!PushEndpointValidator.IsValidPushEndpoint(input.Endpoint, out var uri, out var endpointError))
        {
            return new PushSubscriptionOperationResult(false, endpointError ?? "Ogiltig push-endpoint.");
        }

        var p256dh = input.P256dh?.Trim() ?? string.Empty;
        var auth = input.Auth?.Trim() ?? string.Empty;

        if (p256dh.Length == 0 || p256dh.Length > PushSubscriptionEntity.MaxP256dhLength ||
            auth.Length == 0 || auth.Length > PushSubscriptionEntity.MaxAuthLength)
        {
            return new PushSubscriptionOperationResult(false, "Ogiltiga krypteringsnycklar i push-prenumerationen.");
        }

        try
        {
            var p256dhBytes = Base64Url.DecodeFromChars(p256dh);
            var authBytes = Base64Url.DecodeFromChars(auth);
            if (p256dhBytes.Length != 65 || p256dhBytes[0] != 0x04 || authBytes.Length != 16)
            {
                return new PushSubscriptionOperationResult(false, "Ogiltig nyckellängd i push-prenumerationen.");
            }
        }
        catch
        {
            return new PushSubscriptionOperationResult(false, "Push-nycklarna har ogiltigt Base64Url-format.");
        }

        var normalizedEndpoint = uri!.AbsoluteUri;
        var deviceLabel = NormalizeDeviceLabel(input.DeviceLabel);
        var now = _timeProvider.GetUtcNow();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == normalizedEndpoint, cancellationToken);

        if (existing != null)
        {
            if (!existing.IsActive)
            {
                var activeCount = await db.PushSubscriptions.CountAsync(s => s.IsActive, cancellationToken);
                if (activeCount >= MaxActiveSubscriptions)
                {
                    return new PushSubscriptionOperationResult(
                        false,
                        $"Maxgränsen på {MaxActiveSubscriptions} aktiva enheter har nåtts. Avaktivera en annan enhet först.");
                }
            }

            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.DeviceLabel = deviceLabel ?? existing.DeviceLabel;
            existing.IsActive = true;
            existing.ConsecutiveFailures = 0;
            existing.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Uppdaterade push-prenumeration {SubscriptionId} ({Endpoint}).",
                existing.Id,
                PushEndpointValidator.RedactEndpoint(existing.Endpoint));

            return new PushSubscriptionOperationResult(true, SubscriptionId: existing.Id);
        }

        var currentActiveCount = await db.PushSubscriptions.CountAsync(s => s.IsActive, cancellationToken);
        if (currentActiveCount >= MaxActiveSubscriptions)
        {
            return new PushSubscriptionOperationResult(
                false,
                $"Maxgränsen på {MaxActiveSubscriptions} aktiva enheter har nåtts. Avaktivera en annan enhet först.");
        }

        var entity = new PushSubscriptionEntity
        {
            Endpoint = normalizedEndpoint,
            P256dh = p256dh,
            Auth = auth,
            DeviceLabel = deviceLabel,
            IsActive = true,
            ConsecutiveFailures = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.PushSubscriptions.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Registrerade ny push-prenumeration {SubscriptionId} ({Endpoint}).",
            entity.Id,
            PushEndpointValidator.RedactEndpoint(entity.Endpoint));

        return new PushSubscriptionOperationResult(true, SubscriptionId: entity.Id);
    }

    public async Task<bool> UnsubscribeAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var normalized = endpoint.Trim();
        var now = _timeProvider.GetUtcNow();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var entity = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == normalized, cancellationToken);

        if (entity == null)
            return false;

        entity.IsActive = false;
        entity.UpdatedAt = now;

        var pendingDeliveries = await db.CalendarReminderDeliveries
            .Where(d => d.PushSubscriptionId == entity.Id &&
                        (d.Status == CalendarReminderDeliveryStatus.Pending ||
                         d.Status == CalendarReminderDeliveryStatus.InProgress))
            .ToListAsync(cancellationToken);

        foreach (var delivery in pendingDeliveries)
        {
            delivery.Status = CalendarReminderDeliveryStatus.Cancelled;
            delivery.LeaseToken = null;
            delivery.LockedUntilUtc = null;
            delivery.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Avaktiverade push-prenumeration {SubscriptionId} ({Endpoint}).",
            entity.Id,
            PushEndpointValidator.RedactEndpoint(entity.Endpoint));

        return true;
    }

    public async Task<PushTestNotificationResult> SendTestNotificationAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new PushTestNotificationResult(false, "Ingen aktiv push-prenumeration hittades för den här enheten.");

        var normalized = endpoint.Trim();
        var now = _timeProvider.GetUtcNow();

        if (_lastTestSentByEndpoint.TryGetValue(normalized, out var lastSent) &&
            now - lastSent < TestNotificationCooldown)
        {
            var remaining = (int)Math.Ceiling((TestNotificationCooldown - (now - lastSent)).TotalSeconds);
            return new PushTestNotificationResult(false, $"Vänta {remaining} sekunder innan du skickar en ny testnotis.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var subscription = await db.PushSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Endpoint == normalized && s.IsActive, cancellationToken);

        if (subscription == null)
        {
            return new PushTestNotificationResult(false, "Den här enheten har ingen aktiv push-prenumeration.");
        }

        _lastTestSentByEndpoint[normalized] = now;

        var today = SwedishLocalDateProvider.GetSwedishToday(_timeProvider);
        var payload = JsonSerializer.Serialize(new
        {
            title = "Testnotis från Receptbanken",
            body = "Påminnelser för familjekalendern fungerar på den här enheten.",
            url = $"/kalender?datum={today:yyyy-MM-dd}",
            tag = "receptbanken-test"
        });

        var result = await _webPushClient.SendAsync(subscription, payload, ttlSeconds: 300, cancellationToken);

        await using var updateDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tracked = await updateDb.PushSubscriptions.FirstOrDefaultAsync(s => s.Id == subscription.Id, cancellationToken);
        if (tracked != null)
        {
            if (result.IsSuccess)
            {
                tracked.ConsecutiveFailures = 0;
                tracked.LastSuccessAt = now;
                tracked.UpdatedAt = now;
            }
            else
            {
                tracked.ConsecutiveFailures += 1;
                tracked.LastFailureAt = now;
                tracked.UpdatedAt = now;
                if (result.ShouldDeactivateSubscription)
                {
                    tracked.IsActive = false;
                }
            }
            await updateDb.SaveChangesAsync(cancellationToken);
        }

        if (result.IsSuccess)
        {
            return new PushTestNotificationResult(true, "Testnotis skickad!");
        }

        if (result.ShouldDeactivateSubscription)
        {
            return new PushTestNotificationResult(false, "Push-prenumerationen har gått ut och avaktiverades. Aktivera påminnelser igen.");
        }

        return new PushTestNotificationResult(false, $"Kunde inte skicka testnotis ({result.ErrorMessage ?? "okänt fel"}).");
    }

    private static string? NormalizeDeviceLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var trimmed = label.Trim();
        return trimmed.Length <= PushSubscriptionEntity.MaxDeviceLabelLength
            ? trimmed
            : trimmed[..PushSubscriptionEntity.MaxDeviceLabelLength];
    }
}
