using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Push;

namespace Receptbanken.Tests.Push;

public class WebPushCryptoAndVapidTests : IDisposable
{
    private readonly string _tempDir;

    public WebPushCryptoAndVapidTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rb-push-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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

    [Fact]
    public async Task VapidKeyProvider_ShouldGeneratePersistAndReloadIdenticalKeys()
    {
        var keysPath = Path.Combine(_tempDir, "keys", "vapid.json");
        var options = Options.Create(new VapidOptions
        {
            KeysFilePath = keysPath,
            Subject = "mailto:test@receptbanken.local"
        });

        var provider1 = new VapidKeyProvider(options, NullLogger<VapidKeyProvider>.Instance);
        var generated = await provider1.GetOrCreateKeysAsync();

        File.Exists(keysPath).Should().BeTrue();
        Base64Url.DecodeFromChars(generated.PublicKeyBase64Url).Should().HaveCount(65);
        Base64Url.DecodeFromChars(generated.PublicKeyBase64Url)[0].Should().Be(0x04);
        Base64Url.DecodeFromChars(generated.PrivateKeyBase64Url).Should().HaveCount(32);

        // Simulate restart with a new provider instance
        var provider2 = new VapidKeyProvider(options, NullLogger<VapidKeyProvider>.Instance);
        var reloaded = await provider2.GetOrCreateKeysAsync();

        reloaded.Should().Be(generated);
    }

    [Fact]
    public async Task VapidKeyProvider_ConfigValues_ShouldTakePrecedenceOverFile()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(true);
        var pub = new byte[65];
        pub[0] = 0x04;
        Buffer.BlockCopy(p.Q.X!, 0, pub, 1, 32);
        Buffer.BlockCopy(p.Q.Y!, 0, pub, 33, 32);

        var configPub = Base64Url.EncodeToString(pub);
        var configPriv = Base64Url.EncodeToString(p.D!);

        var keysPath = Path.Combine(_tempDir, "vapid.json");
        await File.WriteAllTextAsync(keysPath, "{\"publicKey\":\"invalid\",\"privateKey\":\"invalid\"}");

        var options = Options.Create(new VapidOptions
        {
            PublicKey = configPub,
            PrivateKey = configPriv,
            Subject = "https://receptbanken.local",
            KeysFilePath = keysPath
        });

        var provider = new VapidKeyProvider(options, NullLogger<VapidKeyProvider>.Instance);
        var keys = await provider.GetOrCreateKeysAsync();

        keys.PublicKeyBase64Url.Should().Be(configPub);
        keys.PrivateKeyBase64Url.Should().Be(configPriv);
        keys.Subject.Should().Be("https://receptbanken.local");
    }

    [Fact]
    public async Task VapidKeyProvider_CorruptedExistingFile_ShouldThrowAndNotOverwrite()
    {
        var keysPath = Path.Combine(_tempDir, "vapid.json");
        const string corruptedContent = "{ not valid json !!!";
        await File.WriteAllTextAsync(keysPath, corruptedContent);

        var options = Options.Create(new VapidOptions
        {
            KeysFilePath = keysPath
        });

        var provider = new VapidKeyProvider(options, NullLogger<VapidKeyProvider>.Instance);
        var act = async () => await provider.GetOrCreateKeysAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await File.ReadAllTextAsync(keysPath)).Should().Be(corruptedContent);
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc123", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc123", true)]
    [InlineData("https://web.push.apple.com/QAbc123", true)]
    [InlineData("https://wns2-bl2p.notify.windows.com/w/?token=abc", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc123", false)]
    [InlineData("https://localhost/push/abc", false)]
    [InlineData("https://127.0.0.1/push/abc", false)]
    [InlineData("https://10.0.0.5/push/abc", false)]
    [InlineData("https://192.168.1.10/push/abc", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://evil.example.com/push/abc", false)]
    [InlineData("https://user:pass@fcm.googleapis.com/fcm/send/abc", false)]
    public void PushEndpointValidator_ShouldEnforceHttpsSsrfAndProviderAllowlist(string endpoint, bool expectedValid)
    {
        var valid = PushEndpointValidator.IsValidPushEndpoint(endpoint, out _, out _);
        valid.Should().Be(expectedValid);
    }

    [Fact]
    public async Task WebPushCrypto_Rfc8291AndRfc8292_ShouldVerifyIndependentlyWithNodeOpenSsl()
    {
        // Generate UA (browser) ECDH P-256 keypair and 16-byte auth secret
        using var uaEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaParams = uaEcdh.ExportParameters(includePrivateParameters: true);
        var uaPublic = new byte[65];
        uaPublic[0] = 0x04;
        Buffer.BlockCopy(uaParams.Q.X!, 0, uaPublic, 1, 32);
        Buffer.BlockCopy(uaParams.Q.Y!, 0, uaPublic, 33, 32);
        var uaPrivate = uaParams.D!;
        var authSecret = RandomNumberGenerator.GetBytes(16);

        // Generate VAPID P-256 keypair
        var keysPath = Path.Combine(_tempDir, "vapid.json");
        var vapidProvider = new VapidKeyProvider(
            Options.Create(new VapidOptions { KeysFilePath = keysPath, Subject = "mailto:family@receptbanken.local" }),
            NullLogger<VapidKeyProvider>.Instance);
        var vapidKeys = await vapidProvider.GetOrCreateKeysAsync();

        var payloadJson = JsonSerializer.Serialize(new
        {
            title = "Tandläkare för Erik",
            body = "14:30 · 2026-11-14 · Folktandvården",
            url = "/kalender?datum=2026-11-14",
            tag = "cal-42-2026-11-14-1794663000"
        });

        var encrypted = WebPushClient.EncryptPayload(
            Encoding.UTF8.GetBytes(payloadJson),
            uaPublic,
            authSecret);

        // Verify aes128gcm header structure
        encrypted.Length.Should().BeGreaterThan(86 + 16);
        BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(16, 4)).Should().Be(4096);
        encrypted[20].Should().Be(65);

        var exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        var jwt = WebPushClient.CreateVapidJwt(
            "https://fcm.googleapis.com",
            vapidKeys.Subject,
            vapidKeys.PrivateKeyBase64Url,
            exp);

        // Independently decrypt the RFC 8291 aes128gcm payload and verify the RFC 8292 ES256 JWT
        // using Node.js built-in OpenSSL crypto module.
        var nodeScript = @"
const crypto = require('crypto');

const input = JSON.parse(process.argv[2]);
const packet = Buffer.from(input.encryptedB64, 'base64');
const uaPrivate = Buffer.from(input.uaPrivateB64, 'base64');
const uaPublic = Buffer.from(input.uaPublicB64, 'base64');
const authSecret = Buffer.from(input.authSecretB64, 'base64');

function hmacSha256(key, data) {
    return crypto.createHmac('sha256', key).update(data).digest();
}

function hkdfExpand(prk, info, length) {
    const t1 = hmacSha256(prk, Buffer.concat([info, Buffer.from([0x01])]));
    return t1.subarray(0, length);
}

// 1. Decrypt RFC 8291 aes128gcm packet
const salt = packet.subarray(0, 16);
const rs = packet.readUInt32BE(16);
const idlen = packet[20];
if (rs !== 4096 || idlen !== 65) throw new Error('Invalid aes128gcm header');
const asPublic = packet.subarray(21, 21 + idlen);
const ciphertextAndTag = packet.subarray(21 + idlen);
const tag = ciphertextAndTag.subarray(ciphertextAndTag.length - 16);
const ciphertext = ciphertextAndTag.subarray(0, ciphertextAndTag.length - 16);

const ecdh = crypto.createECDH('prime256v1');
ecdh.setPrivateKey(uaPrivate);
const ecdhSecret = ecdh.computeSecret(asPublic);

const prkKey = hmacSha256(authSecret, ecdhSecret);
const keyInfo = Buffer.concat([Buffer.from('WebPush: info\x00', 'ascii'), uaPublic, asPublic]);
const ikm = hkdfExpand(prkKey, keyInfo, 32);
const prk = hmacSha256(salt, ikm);
const cek = hkdfExpand(prk, Buffer.from('Content-Encoding: aes128gcm\x00', 'ascii'), 16);
const nonce = hkdfExpand(prk, Buffer.from('Content-Encoding: nonce\x00', 'ascii'), 12);

const decipher = crypto.createDecipheriv('aes-128-gcm', cek, nonce);
decipher.setAuthTag(tag);
const decryptedPadded = Buffer.concat([decipher.update(ciphertext), decipher.final()]);
if (decryptedPadded[decryptedPadded.length - 1] !== 0x02) throw new Error('Missing 0x02 record delimiter');
const plaintext = decryptedPadded.subarray(0, decryptedPadded.length - 1).toString('utf8');

// 2. Verify RFC 8292 ES256 VAPID JWT
const jwtParts = input.jwt.split('.');
if (jwtParts.length !== 3) throw new Error('Invalid JWT format');
const signingInput = Buffer.from(jwtParts[0] + '.' + jwtParts[1], 'ascii');
const rawSig = Buffer.from(jwtParts[2].replace(/-/g, '+').replace(/_/g, '/'), 'base64');
if (rawSig.length !== 64) throw new Error('Invalid P1363 signature length');

function encodeDerInt(buf) {
    let i = 0;
    while (i < buf.length - 1 && buf[i] === 0) i++;
    let slice = buf.subarray(i);
    if (slice[0] & 0x80) slice = Buffer.concat([Buffer.from([0x00]), slice]);
    return Buffer.concat([Buffer.from([0x02, slice.length]), slice]);
}
const rDer = encodeDerInt(rawSig.subarray(0, 32));
const sDer = encodeDerInt(rawSig.subarray(32, 64));
const derSig = Buffer.concat([Buffer.from([0x30, rDer.length + sDer.length]), rDer, sDer]);

const vapidPubRaw = Buffer.from(input.vapidPublicB64Url.replace(/-/g, '+').replace(/_/g, '/'), 'base64');
const spkiPrefix = Buffer.from('3059301306072a8648ce3d020106082a8648ce3d030107034200', 'hex');
const publicKeyObj = crypto.createPublicKey({
    key: Buffer.concat([spkiPrefix, vapidPubRaw]),
    format: 'der',
    type: 'spki'
});

const verifier = crypto.createVerify('SHA256');
verifier.update(signingInput);
const jwtValid = verifier.verify(publicKeyObj, derSig);
if (!jwtValid) throw new Error('VAPID JWT signature verification failed');

const jwtPayload = JSON.parse(Buffer.from(jwtParts[1].replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8'));
console.log(JSON.stringify({ plaintext, jwtPayload, jwtValid }));
";

        var argObj = JsonSerializer.Serialize(new
        {
            encryptedB64 = Convert.ToBase64String(encrypted),
            uaPrivateB64 = Convert.ToBase64String(uaPrivate),
            uaPublicB64 = Convert.ToBase64String(uaPublic),
            authSecretB64 = Convert.ToBase64String(authSecret),
            jwt,
            vapidPublicB64Url = vapidKeys.PublicKeyBase64Url
        });

        var scriptPath = Path.Combine(_tempDir, "verify_webpush.js");
        await File.WriteAllTextAsync(scriptPath, nodeScript);

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(argObj);

        using var process = Process.Start(psi);
        process.Should().NotBeNull();
        var stdout = await process!.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(0, $"Node OpenSSL verification failed: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("plaintext").GetString().Should().Be(payloadJson);
        doc.RootElement.GetProperty("jwtValid").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("jwtPayload").GetProperty("aud").GetString().Should().Be("https://fcm.googleapis.com");
        doc.RootElement.GetProperty("jwtPayload").GetProperty("sub").GetString().Should().Be("mailto:family@receptbanken.local");
    }

    [Fact]
    public async Task WebPushClient_SendAsync_ShouldHandle201_410_And_503_Correctly()
    {
        var keysPath = Path.Combine(_tempDir, "vapid.json");
        var vapidProvider = new VapidKeyProvider(
            Options.Create(new VapidOptions { KeysFilePath = keysPath }),
            NullLogger<VapidKeyProvider>.Instance);

        using var uaEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaParams = uaEcdh.ExportParameters(false);
        var uaPub = new byte[65];
        uaPub[0] = 0x04;
        Buffer.BlockCopy(uaParams.Q.X!, 0, uaPub, 1, 32);
        Buffer.BlockCopy(uaParams.Q.Y!, 0, uaPub, 33, 32);

        var sub = new PushSubscriptionEntity
        {
            Id = 1,
            Endpoint = "https://fcm.googleapis.com/fcm/send/test-token",
            P256dh = Base64Url.EncodeToString(uaPub),
            Auth = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            IsActive = true
        };

        var statusCode = HttpStatusCode.Created;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
        using var httpClient = new HttpClient(handler);
        var client = new WebPushClient(httpClient, vapidProvider, NullLogger<WebPushClient>.Instance);

        var okResult = await client.SendAsync(sub, "{\"title\":\"Hej\"}");
        okResult.IsSuccess.Should().BeTrue();
        okResult.StatusCode.Should().Be(201);

        statusCode = HttpStatusCode.Gone;
        var goneResult = await client.SendAsync(sub, "{\"title\":\"Hej\"}");
        goneResult.IsSuccess.Should().BeFalse();
        goneResult.StatusCode.Should().Be(410);
        goneResult.ShouldDeactivateSubscription.Should().BeTrue();

        statusCode = HttpStatusCode.ServiceUnavailable;
        var retryResult = await client.SendAsync(sub, "{\"title\":\"Hej\"}");
        retryResult.IsSuccess.Should().BeFalse();
        retryResult.StatusCode.Should().Be(503);
        retryResult.IsTransientFailure.Should().BeTrue();
        retryResult.ShouldDeactivateSubscription.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/token-1", 900, "900")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/token-2", 300, "300")]
    [InlineData("https://web.push.apple.com/QAbc123", -10, "0")]
    [InlineData("https://fcm.googleapis.com/fcm/send/token-4", 100000, "86400")]
    public async Task WebPushClient_SendAsync_ShouldIncludeRequiredVapidAndWebPushHeaders(
        string endpoint,
        int ttlSeconds,
        string expectedTtlHeader)
    {
        var fixedNow = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedNow);

        var keysPath = Path.Combine(_tempDir, $"vapid-{Guid.NewGuid():N}.json");
        var vapidProvider = new VapidKeyProvider(
            Options.Create(new VapidOptions
            {
                KeysFilePath = keysPath,
                Subject = "mailto:family@receptbanken.local"
            }),
            NullLogger<VapidKeyProvider>.Instance);
        var vapidKeys = await vapidProvider.GetOrCreateKeysAsync();

        using var uaEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaParams = uaEcdh.ExportParameters(false);
        var uaPub = new byte[65];
        uaPub[0] = 0x04;
        Buffer.BlockCopy(uaParams.Q.X!, 0, uaPub, 1, 32);
        Buffer.BlockCopy(uaParams.Q.Y!, 0, uaPub, 33, 32);

        var sub = new PushSubscriptionEntity
        {
            Id = 1,
            Endpoint = endpoint,
            P256dh = Base64Url.EncodeToString(uaPub),
            Auth = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            IsActive = true
        };

        CapturedPushRequest? captured = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            captured = CaptureRequest(req);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        using var httpClient = new HttpClient(handler);
        var client = new WebPushClient(httpClient, vapidProvider, NullLogger<WebPushClient>.Instance, timeProvider);

        var result = await client.SendAsync(sub, "{\"title\":\"Hej\"}", ttlSeconds);

        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(201);
        captured.Should().NotBeNull();

        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri.Should().Be(new Uri(endpoint));
        captured.Ttl.Should().Be(expectedTtlHeader);
        captured.Urgency.Should().Be("normal");
        captured.ContentType.Should().Be("application/octet-stream");
        captured.ContentEncoding.Should().Equal("aes128gcm");
        captured.BodyLength.Should().BeGreaterThan(86 + 16);

        // Verify VAPID Authorization header: "vapid t=<jwt>, k=<publicKey>"
        captured.Authorization.Should().StartWith("vapid ");
        var parts = captured.Authorization!["vapid ".Length..].Split(", ", StringSplitOptions.TrimEntries);
        parts.Should().HaveCount(2);
        parts[0].Should().StartWith("t=");
        parts[1].Should().Be($"k={vapidKeys.PublicKeyBase64Url}");

        var jwt = parts[0][2..];
        var jwtSegments = jwt.Split('.');
        jwtSegments.Should().HaveCount(3);

        var headerJson = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(jwtSegments[0]));
        using (var headerDoc = JsonDocument.Parse(headerJson))
        {
            headerDoc.RootElement.GetProperty("typ").GetString().Should().Be("JWT");
            headerDoc.RootElement.GetProperty("alg").GetString().Should().Be("ES256");
        }

        var payloadJson = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(jwtSegments[1]));
        var expectedAudience = $"{new Uri(endpoint).Scheme}://{new Uri(endpoint).Authority}";
        using (var payloadDoc = JsonDocument.Parse(payloadJson))
        {
            payloadDoc.RootElement.GetProperty("aud").GetString().Should().Be(expectedAudience);
            payloadDoc.RootElement.GetProperty("sub").GetString().Should().Be("mailto:family@receptbanken.local");
            payloadDoc.RootElement.GetProperty("exp").GetInt64().Should().Be(fixedNow.AddHours(12).ToUnixTimeSeconds());
        }

        var pubBytes = Base64Url.DecodeFromChars(vapidKeys.PublicKeyBase64Url);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = pubBytes[1..33],
                Y = pubBytes[33..65]
            }
        });
        var signingInput = Encoding.ASCII.GetBytes($"{jwtSegments[0]}.{jwtSegments[1]}");
        var signature = Base64Url.DecodeFromChars(jwtSegments[2]);
        verifier.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue();
    }

    private static CapturedPushRequest CaptureRequest(HttpRequestMessage request)
    {
        var auth = request.Headers.TryGetValues("Authorization", out var authValues)
            ? string.Join(", ", authValues)
            : null;
        var ttl = request.Headers.TryGetValues("TTL", out var ttlValues)
            ? string.Join(", ", ttlValues)
            : null;
        var urgency = request.Headers.TryGetValues("Urgency", out var urgencyValues)
            ? string.Join(", ", urgencyValues)
            : null;

        var contentType = request.Content?.Headers.ContentType?.MediaType;
        var contentEncoding = request.Content?.Headers.ContentEncoding.ToArray() ?? [];
        var bodyBytes = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? [];

        return new CapturedPushRequest(
            request.Method,
            request.RequestUri,
            auth,
            ttl,
            urgency,
            contentType,
            contentEncoding,
            bodyBytes.Length);
    }

    private sealed record CapturedPushRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? Authorization,
        string? Ttl,
        string? Urgency,
        string? ContentType,
        string[] ContentEncoding,
        int BodyLength);

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
