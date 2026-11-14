using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Models;

namespace Receptbanken.Infrastructure.Push;

public sealed record WebPushSendResult(
    bool IsSuccess,
    int? StatusCode,
    string? ErrorMessage,
    bool ShouldDeactivateSubscription,
    bool IsTransientFailure);

public interface IWebPushClient
{
    Task<WebPushSendResult> SendAsync(
        PushSubscriptionEntity subscription,
        string payloadJson,
        int ttlSeconds = 900,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends encrypted Web Push notifications using built-in .NET 10 cryptography
/// (RFC 8291 aes128gcm payload encryption and RFC 8292 VAPID ES256 JWT authentication).
/// </summary>
public sealed class WebPushClient : IWebPushClient
{
    public const int MaxPayloadBytes = 3800;
    public const uint DefaultRecordSize = 4096;

    private static readonly byte[] WebPushInfoPrefix = "WebPush: info\0"u8.ToArray();
    private static readonly byte[] CekInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();
    private static readonly byte[] VapidJwtHeaderBytes = "{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8.ToArray();

    private readonly HttpClient _httpClient;
    private readonly IVapidKeyProvider _vapidKeyProvider;
    private readonly ILogger<WebPushClient> _logger;
    private readonly TimeProvider _timeProvider;

    public WebPushClient(
        HttpClient httpClient,
        IVapidKeyProvider vapidKeyProvider,
        ILogger<WebPushClient> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _vapidKeyProvider = vapidKeyProvider;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WebPushSendResult> SendAsync(
        PushSubscriptionEntity subscription,
        string payloadJson,
        int ttlSeconds = 900,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        if (!PushEndpointValidator.IsValidPushEndpoint(subscription.Endpoint, out var endpointUri, out var endpointError))
        {
            return new WebPushSendResult(
                IsSuccess: false,
                StatusCode: null,
                ErrorMessage: endpointError ?? "Ogiltig push-endpoint.",
                ShouldDeactivateSubscription: true,
                IsTransientFailure: false);
        }

        var plaintext = Encoding.UTF8.GetBytes(payloadJson);
        if (plaintext.Length > MaxPayloadBytes)
        {
            return new WebPushSendResult(
                IsSuccess: false,
                StatusCode: null,
                ErrorMessage: $"Push-payload överskrider maxgränsen på {MaxPayloadBytes} byte.",
                ShouldDeactivateSubscription: false,
                IsTransientFailure: false);
        }

        byte[] uaPublic;
        byte[] authSecret;
        try
        {
            uaPublic = Base64Url.DecodeFromChars(subscription.P256dh);
            authSecret = Base64Url.DecodeFromChars(subscription.Auth);
        }
        catch (Exception)
        {
            return new WebPushSendResult(
                IsSuccess: false,
                StatusCode: null,
                ErrorMessage: "Prenumerationens krypteringsnycklar har ogiltigt format.",
                ShouldDeactivateSubscription: true,
                IsTransientFailure: false);
        }

        if (uaPublic.Length != 65 || uaPublic[0] != 0x04 || authSecret.Length != 16)
        {
            return new WebPushSendResult(
                IsSuccess: false,
                StatusCode: null,
                ErrorMessage: "Prenumerationens P256dh- eller Auth-nyckel har ogiltig längd.",
                ShouldDeactivateSubscription: true,
                IsTransientFailure: false);
        }

        var keys = await _vapidKeyProvider.GetOrCreateKeysAsync(cancellationToken);
        var encryptedBody = EncryptPayload(plaintext, uaPublic, authSecret);
        var audience = $"{endpointUri!.Scheme}://{endpointUri.Authority}";
        var jwt = CreateVapidJwt(
            audience,
            keys.Subject,
            keys.PrivateKeyBase64Url,
            _timeProvider.GetUtcNow().AddHours(12).ToUnixTimeSeconds());

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={jwt}, k={keys.PublicKeyBase64Url}");
        request.Headers.TryAddWithoutValidation("TTL", Math.Clamp(ttlSeconds, 0, 86400).ToString());
        request.Headers.TryAddWithoutValidation("Urgency", "normal");

        var content = new ByteArrayContent(encryptedBody);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentEncoding.Add("aes128gcm");
        request.Content = content;

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return new WebPushSendResult(
                    IsSuccess: true,
                    StatusCode: statusCode,
                    ErrorMessage: null,
                    ShouldDeactivateSubscription: false,
                    IsTransientFailure: false);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                _logger.LogInformation(
                    "Push-prenumeration {SubscriptionId} ({Endpoint}) returnerade HTTP {StatusCode} och markeras som inaktiv.",
                    subscription.Id,
                    PushEndpointValidator.RedactEndpoint(subscription.Endpoint),
                    statusCode);

                return new WebPushSendResult(
                    IsSuccess: false,
                    StatusCode: statusCode,
                    ErrorMessage: $"HTTP {statusCode}",
                    ShouldDeactivateSubscription: true,
                    IsTransientFailure: false);
            }

            var isTransient = statusCode == 429 || statusCode >= 500;
            _logger.LogWarning(
                "Push-utskick till {SubscriptionId} ({Endpoint}) misslyckades med HTTP {StatusCode}.",
                subscription.Id,
                PushEndpointValidator.RedactEndpoint(subscription.Endpoint),
                statusCode);

            return new WebPushSendResult(
                IsSuccess: false,
                StatusCode: statusCode,
                ErrorMessage: $"HTTP {statusCode}",
                ShouldDeactivateSubscription: false,
                IsTransientFailure: isTransient);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Nätverksfel vid push-utskick till {SubscriptionId} ({Endpoint}).",
                subscription.Id,
                PushEndpointValidator.RedactEndpoint(subscription.Endpoint));

            return new WebPushSendResult(
                IsSuccess: false,
                StatusCode: null,
                ErrorMessage: ex.GetType().Name,
                ShouldDeactivateSubscription: false,
                IsTransientFailure: true);
        }
    }

    /// <summary>
    /// Encrypts a Web Push payload according to RFC 8291 using aes128gcm content coding.
    /// </summary>
    public static byte[] EncryptPayload(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> uaPublic,
        ReadOnlySpan<byte> authSecret,
        ReadOnlySpan<byte> saltOverride = default,
        byte[]? asPrivateKeyOverride = null)
    {
        if (uaPublic.Length != 65 || uaPublic[0] != 0x04)
            throw new ArgumentException("uaPublic måste vara en 65-byte okomprimerad P-256-nyckel.", nameof(uaPublic));

        if (authSecret.Length != 16)
            throw new ArgumentException("authSecret måste vara 16 byte.", nameof(authSecret));

        var salt = new byte[16];
        if (!saltOverride.IsEmpty)
        {
            if (saltOverride.Length != 16)
                throw new ArgumentException("saltOverride måste vara 16 byte.", nameof(saltOverride));
            saltOverride.CopyTo(salt);
        }
        else
        {
            RandomNumberGenerator.Fill(salt);
        }

        using var asEcdh = asPrivateKeyOverride != null
            ? CreateEcdhFromPrivateKey(asPrivateKeyOverride)
            : ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var asParams = asEcdh.ExportParameters(includePrivateParameters: false);
        var asPublic = new byte[65];
        asPublic[0] = 0x04;
        Buffer.BlockCopy(asParams.Q.X!, 0, asPublic, 1, 32);
        Buffer.BlockCopy(asParams.Q.Y!, 0, asPublic, 33, 32);

        using var uaEcdh = CreateEcdhFromPublicKey(uaPublic);
        var ecdhSecret = asEcdh.DeriveRawSecretAgreement(uaEcdh.PublicKey);

        // RFC 8291 Section 3.3:
        // PRK_key = HMAC-SHA-256(auth_secret, ecdh_secret)
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, authSecret.ToArray());

        // key_info = "WebPush: info" || 0x00 || ua_public || as_public
        var keyInfo = new byte[WebPushInfoPrefix.Length + 65 + 65];
        WebPushInfoPrefix.CopyTo(keyInfo, 0);
        uaPublic.CopyTo(keyInfo.AsSpan(WebPushInfoPrefix.Length));
        asPublic.CopyTo(keyInfo.AsSpan(WebPushInfoPrefix.Length + 65));

        // IKM = HMAC-SHA-256(PRK_key, key_info || 0x01)
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

        // PRK = HMAC-SHA-256(salt, IKM)
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);

        // CEK = HMAC-SHA-256(PRK, "Content-Encoding: aes128gcm" || 0x00 || 0x01)[0..16]
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);

        // NONCE = HMAC-SHA-256(PRK, "Content-Encoding: nonce" || 0x00 || 0x01)[0..12]
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        // Plaintext with 0x02 delimiter (final record)
        var paddedPlaintext = new byte[plaintext.Length + 1];
        plaintext.CopyTo(paddedPlaintext);
        paddedPlaintext[^1] = 0x02;

        var ciphertext = new byte[paddedPlaintext.Length];
        var tag = new byte[16];
        using (var aesGcm = new AesGcm(cek, 16))
        {
            aesGcm.Encrypt(nonce, paddedPlaintext, ciphertext, tag);
        }

        // aes128gcm header: salt (16) || rs (4 BE) || idlen (1) || keyid (65)
        const int headerLength = 16 + 4 + 1 + 65;
        var output = new byte[headerLength + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(salt, 0, output, 0, 16);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16, 4), DefaultRecordSize);
        output[20] = 65;
        Buffer.BlockCopy(asPublic, 0, output, 21, 65);
        Buffer.BlockCopy(ciphertext, 0, output, headerLength, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, headerLength + ciphertext.Length, tag.Length);

        return output;
    }

    /// <summary>
    /// Creates an RFC 8292 VAPID ES256 JWT token.
    /// </summary>
    public static string CreateVapidJwt(
        string audience,
        string subject,
        string privateKeyBase64Url,
        long expirationUnixSeconds)
    {
        var privBytes = Base64Url.DecodeFromChars(privateKeyBase64Url);
        if (privBytes.Length != 32)
            throw new ArgumentException("VAPID-privatnyckel måste vara 32 byte.", nameof(privateKeyBase64Url));

        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = audience,
            exp = expirationUnixSeconds,
            sub = subject
        });

        var headerB64 = Base64Url.EncodeToString(VapidJwtHeaderBytes);
        var payloadB64 = Base64Url.EncodeToString(payloadJson);
        var signingInput = $"{headerB64}.{payloadB64}";

        var ecParams = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privBytes
        };

        using var ecdsa = ECDsa.Create(ecParams);
        var signature = ecdsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var signatureB64 = Base64Url.EncodeToString(signature);
        return $"{signingInput}.{signatureB64}";
    }

    private static ECDiffieHellman CreateEcdhFromPrivateKey(byte[] privateKey)
    {
        if (privateKey.Length != 32)
            throw new ArgumentException("Privat P-256-nyckel måste vara 32 byte.", nameof(privateKey));

        // Derive public point Q via ECDsa so ECDiffieHellman.Create(ECParameters) has both Q and D.
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey
        });
        var fullParams = ecdsa.ExportParameters(includePrivateParameters: true);
        return ECDiffieHellman.Create(fullParams);
    }

    private static ECDiffieHellman CreateEcdhFromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        var ecParams = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey[1..33].ToArray(),
                Y = publicKey[33..65].ToArray()
            }
        };
        return ECDiffieHellman.Create(ecParams);
    }
}
