using System.Buffers.Text;
using System.Security.Cryptography;
using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Push;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Calendar;

namespace Receptbanken.Tests.Components;

public class CalendarPushSettingsCardTests : TestContext
{
    private sealed class FakeVapidKeyProvider : IVapidKeyProvider
    {
        public Task<VapidKeyPair> GetOrCreateKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VapidKeyPair("BTestVapidPublicKey", "TestPrivateKey", VapidOptions.DefaultSubject));
    }

    private sealed class FakeWebPushClient : IWebPushClient
    {
        public int SendCount { get; private set; }
        public Task<WebPushSendResult> SendAsync(
            PushSubscriptionEntity subscription,
            string payloadJson,
            int ttlSeconds = 900,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(new WebPushSendResult(true, 201, null, false, false));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private (TestDbContextFactory DbFactory, PushSubscriptionService Service, FakeWebPushClient Client, FakeTimeProvider Time) SetupPushServices()
    {
        var dbFactory = new TestDbContextFactory();
        var client = new FakeWebPushClient();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var service = new PushSubscriptionService(
            dbFactory,
            new FakeVapidKeyProvider(),
            client,
            NullLogger<PushSubscriptionService>.Instance,
            time);

        Services.AddSingleton(service);
        return (dbFactory, service, client, time);
    }

    private static (string P256dh, string Auth) GenerateValidBrowserKeys()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdh.ExportParameters(false);
        var pub = new byte[65];
        pub[0] = 0x04;
        Buffer.BlockCopy(p.Q.X!, 0, pub, 1, 32);
        Buffer.BlockCopy(p.Q.Y!, 0, pub, 33, 32);
        return (Base64Url.EncodeToString(pub), Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public void Card_WhenIosNotStandalone_ShouldShowSwedishPwaInstallInstructions()
    {
        var (dbFactory, _, _, _) = SetupPushServices();
        using var _ = dbFactory;

        JSInterop.Setup<CalendarPushSettingsCard.PushEnvironmentState>("receptbankenPush.getEnvironmentState")
            .SetResult(new CalendarPushSettingsCard.PushEnvironmentState
            {
                IsSupported = false,
                IsIos = true,
                IsStandalone = false,
                Permission = "default"
            });

        var cut = RenderComponent<CalendarPushSettingsCard>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Lägg till på hemskärmen");
            cut.Markup.Should().Contain("Stöds ej");
        });
    }

    [Fact]
    public void Card_WhenSupportedAndNotSubscribed_ShouldBindDirectJsEnableButtonWithoutBlazorOnClick()
    {
        var (dbFactory, _, _, _) = SetupPushServices();
        using var _ = dbFactory;

        JSInterop.Setup<CalendarPushSettingsCard.PushEnvironmentState>("receptbankenPush.getEnvironmentState")
            .SetResult(new CalendarPushSettingsCard.PushEnvironmentState
            {
                IsSupported = true,
                IsIos = false,
                IsStandalone = false,
                Permission = "default",
                Endpoint = null
            });
        JSInterop.SetupVoid("receptbankenPush.bindEnableButton", _ => true);
        JSInterop.SetupVoid("receptbankenPush.unbindEnableButton", _ => true);

        var cut = RenderComponent<CalendarPushSettingsCard>();

        cut.WaitForAssertion(() =>
        {
            var enableBtn = cut.Find(".calendar-push-enable-btn");
            enableBtn.TextContent.Should().Contain("Aktivera påminnelser");
            JSInterop.Invocations.Should().Contain(i => i.Identifier == "receptbankenPush.bindEnableButton");
        });
    }

    [Fact]
    public async Task Card_OnPushSubscribedFromJs_ShouldPersistAndShowSubscribedButtons()
    {
        var (dbFactory, _, client, _) = SetupPushServices();
        using var _ = dbFactory;

        var (p256dh, auth) = GenerateValidBrowserKeys();
        const string endpoint = "https://fcm.googleapis.com/fcm/send/device-123";

        JSInterop.Setup<CalendarPushSettingsCard.PushEnvironmentState>("receptbankenPush.getEnvironmentState")
            .SetResult(new CalendarPushSettingsCard.PushEnvironmentState
            {
                IsSupported = true,
                IsIos = false,
                IsStandalone = false,
                Permission = "default",
                Endpoint = null
            });
        JSInterop.SetupVoid("receptbankenPush.bindEnableButton", _ => true);
        JSInterop.SetupVoid("receptbankenPush.unbindEnableButton", _ => true);

        var cut = RenderComponent<CalendarPushSettingsCard>();
        cut.WaitForAssertion(() => cut.Find(".calendar-push-enable-btn").Should().NotBeNull());

        JSInterop.Setup<CalendarPushSettingsCard.PushEnvironmentState>("receptbankenPush.getEnvironmentState")
            .SetResult(new CalendarPushSettingsCard.PushEnvironmentState
            {
                IsSupported = true,
                IsIos = false,
                IsStandalone = false,
                Permission = "granted",
                Endpoint = endpoint
            });
        await cut.InvokeAsync(() => cut.Instance.OnPushSubscribedFromJs(new CalendarPushSettingsCard.PushSubscriptionJsPayload
        {
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            DeviceLabel = "iPhone"
        }));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Aktiverad");
            cut.Markup.Should().Contain("Påminnelser är aktiverade på den här enheten.");
            cut.Markup.Should().NotContain("aktiva enheter");
            cut.Find(".calendar-push-test-btn").Should().NotBeNull();
            cut.Find(".calendar-push-disable-btn").Should().NotBeNull();
        });

        // Clicking test notification button sends a test push
        cut.Find(".calendar-push-test-btn").Click();
        cut.WaitForAssertion(() =>
        {
            client.SendCount.Should().Be(1);
            cut.Markup.Should().Contain("Testnotis skickad!");
        });
    }

    [Fact]
    public async Task PushSubscriptionService_ShouldEnforce25DeviceLimit_AndTestCooldown()
    {
        var (dbFactory, service, client, time) = SetupPushServices();
        using var _ = dbFactory;

        var (p256dh, auth) = GenerateValidBrowserKeys();

        for (var i = 1; i <= PushSubscriptionService.MaxActiveSubscriptions; i++)
        {
            var res = await service.SubscribeOrUpdateAsync(new PushSubscriptionInput(
                $"https://fcm.googleapis.com/fcm/send/dev-{i}",
                p256dh,
                auth,
                $"Enhet {i}"));
            res.Success.Should().BeTrue();
        }

        // 26th device must fail with clear message without removing any existing device
        var overLimit = await service.SubscribeOrUpdateAsync(new PushSubscriptionInput(
            "https://fcm.googleapis.com/fcm/send/dev-26",
            p256dh,
            auth,
            "Enhet 26"));
        overLimit.Success.Should().BeFalse();
        overLimit.ErrorMessage.Should().Contain("25");

        using (var db = dbFactory.CreateDbContext())
        {
            (await db.PushSubscriptions.CountAsync(s => s.IsActive)).Should().Be(25);
        }

        // Test notification cooldown
        const string firstEndpoint = "https://fcm.googleapis.com/fcm/send/dev-1";
        var test1 = await service.SendTestNotificationAsync(firstEndpoint);
        test1.Success.Should().BeTrue();
        client.SendCount.Should().Be(1);

        var test2 = await service.SendTestNotificationAsync(firstEndpoint);
        test2.Success.Should().BeFalse();
        test2.Message.Should().Contain("Vänta");
        client.SendCount.Should().Be(1);

        time.Advance(PushSubscriptionService.TestNotificationCooldown);
        var test3 = await service.SendTestNotificationAsync(firstEndpoint);
        test3.Success.Should().BeTrue();
        client.SendCount.Should().Be(2);
    }

    [Fact]
    public void ServiceWorkerAndPushJs_ShouldMeetSecurityAndIosGestureRequirements()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var swPath = Path.Combine(repoRoot, "src", "Receptbanken.Web", "wwwroot", "sw.js");
        var pushJsPath = Path.Combine(repoRoot, "src", "Receptbanken.Web", "wwwroot", "js", "calendar-push.js");

        var swContent = File.ReadAllText(swPath);
        swContent.Should().Contain("addEventListener('push'");
        swContent.Should().Contain("addEventListener('notificationclick'");
        swContent.Should().Contain("'/kalender'");
        swContent.Should().NotContain("addEventListener('fetch'");
        swContent.Should().NotContain("caches.");

        var pushJsContent = File.ReadAllText(pushJsPath);
        pushJsContent.Should().Contain("buttonEl.addEventListener('click'");
        pushJsContent.Should().Contain("Notification.requestPermission()");
    }
}
