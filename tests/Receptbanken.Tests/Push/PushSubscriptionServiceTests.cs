using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Push;
using Receptbanken.Tests.Infrastructure;

namespace Receptbanken.Tests.Push;

public class PushSubscriptionServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly FakeTimeProvider _timeProvider;
    private readonly string _tempDir;
    private readonly VapidKeyProvider _vapidKeyProvider;

    public PushSubscriptionServiceTests()
    {
        _dbFactory = new TestDbContextFactory();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
        _tempDir = Path.Combine(Path.GetTempPath(), $"rb-push-sub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _vapidKeyProvider = new VapidKeyProvider(
            Options.Create(new VapidOptions
            {
                KeysFilePath = Path.Combine(_tempDir, "vapid.json"),
                Subject = "mailto:test@receptbanken.local"
            }),
            NullLogger<VapidKeyProvider>.Instance);
    }

    public void Dispose()
    {
        _dbFactory.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Theory]
    [InlineData(1, 14)]
    [InlineData(14, 1)]
    public async Task SendTestNotificationAsync_ShouldEnforce15SecondCooldownPerEndpoint(
        int elapsedSeconds,
        int expectedRemainingSeconds)
    {
        var client = new StubWebPushClient((_, _) => new WebPushSendResult(true, 201, null, false, false));
        var service = CreateService(client);

        const string endpoint1 = "https://fcm.googleapis.com/fcm/send/cooldown-1";
        const string endpoint2 = "https://fcm.googleapis.com/fcm/send/cooldown-2";
        await SubscribeValidAsync(service, endpoint1);
        await SubscribeValidAsync(service, endpoint2);

        // First call succeeds
        var first = await service.SendTestNotificationAsync(endpoint1);
        first.Success.Should().BeTrue();
        client.CallCount.Should().Be(1);

        // Second call within 15s is blocked without calling WebPushClient
        _timeProvider.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        var blocked = await service.SendTestNotificationAsync(endpoint1);
        blocked.Success.Should().BeFalse();
        blocked.Message.Should().Contain($"Vänta {expectedRemainingSeconds} sekunder");
        client.CallCount.Should().Be(1);

        // Different endpoint is not blocked by endpoint1's cooldown
        var otherEndpoint = await service.SendTestNotificationAsync(endpoint2);
        otherEndpoint.Success.Should().BeTrue();
        client.CallCount.Should().Be(2);

        // Advancing past full 15s from first call allows endpoint1 again
        _timeProvider.Advance(TimeSpan.FromSeconds(15 - elapsedSeconds));
        var afterCooldown = await service.SendTestNotificationAsync(endpoint1);
        afterCooldown.Success.Should().BeTrue();
        client.CallCount.Should().Be(3);

        using var db = _dbFactory.CreateDbContext();
        var sub1 = await db.PushSubscriptions.SingleAsync(s => s.Endpoint == endpoint1);
        sub1.ConsecutiveFailures.Should().Be(0);
        sub1.LastSuccessAt.Should().Be(_timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task SendTestNotificationAsync_WhenEndpointReturnsHttp410_ShouldDeactivateSubscription()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone));
        using var httpClient = new HttpClient(handler);
        var realClient = new WebPushClient(
            httpClient,
            _vapidKeyProvider,
            NullLogger<WebPushClient>.Instance,
            _timeProvider);

        var service = CreateService(realClient);
        const string endpoint = "https://fcm.googleapis.com/fcm/send/expired-410";
        await SubscribeValidAsync(service, endpoint);

        var result = await service.SendTestNotificationAsync(endpoint);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("avaktiverades");

        using var db = _dbFactory.CreateDbContext();
        var sub = await db.PushSubscriptions.SingleAsync(s => s.Endpoint == endpoint);
        sub.IsActive.Should().BeFalse();
        sub.ConsecutiveFailures.Should().Be(1);
        sub.LastFailureAt.Should().Be(_timeProvider.GetUtcNow());

        var status = await service.GetStatusAsync(endpoint);
        status.IsSubscribed.Should().BeFalse();
        status.ActiveDeviceCount.Should().Be(0);
    }

    private PushSubscriptionService CreateService(IWebPushClient client) =>
        new(_dbFactory, _vapidKeyProvider, client, NullLogger<PushSubscriptionService>.Instance, _timeProvider);

    private static async Task SubscribeValidAsync(PushSubscriptionService service, string endpoint)
    {
        using var uaEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaParams = uaEcdh.ExportParameters(false);
        var uaPub = new byte[65];
        uaPub[0] = 0x04;
        Buffer.BlockCopy(uaParams.Q.X!, 0, uaPub, 1, 32);
        Buffer.BlockCopy(uaParams.Q.Y!, 0, uaPub, 33, 32);

        var result = await service.SubscribeOrUpdateAsync(new PushSubscriptionInput(
            Endpoint: endpoint,
            P256dh: Base64Url.EncodeToString(uaPub),
            Auth: Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            DeviceLabel: "Test-enhet"));
        result.Success.Should().BeTrue();
    }

    private sealed class StubWebPushClient(Func<PushSubscriptionEntity, string, WebPushSendResult> responder) : IWebPushClient
    {
        public int CallCount { get; private set; }

        public Task<WebPushSendResult> SendAsync(
            PushSubscriptionEntity subscription,
            string payloadJson,
            int ttlSeconds = 900,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(responder(subscription, payloadJson));
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class FakeTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
