using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Receptbanken.Infrastructure.Push;

public sealed class VapidOptions
{
    public const string DefaultSubject = "mailto:noreply@receptbanken.local";

    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }
    public string Subject { get; set; } = DefaultSubject;
    public string KeysFilePath { get; set; } = "/app/data/keys/vapid.json";
}

public sealed record VapidKeyPair(
    string PublicKeyBase64Url,
    string PrivateKeyBase64Url,
    string Subject);

public interface IVapidKeyProvider
{
    Task<VapidKeyPair> GetOrCreateKeysAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves or generates persistent P-256 VAPID keys for Web Push authentication (RFC 8292).
/// Priority: (1) valid configuration values, (2) existing vapid.json on persistent storage,
/// (3) newly generated P-256 keypair written atomically with restricted file permissions.
/// </summary>
public sealed class VapidKeyProvider : IVapidKeyProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly VapidOptions _options;
    private readonly ILogger<VapidKeyProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VapidKeyPair? _cached;

    public VapidKeyProvider(IOptions<VapidOptions> options, ILogger<VapidKeyProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VapidKeyPair> GetOrCreateKeysAsync(CancellationToken cancellationToken = default)
    {
        if (_cached != null)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached != null)
                return _cached;

            var subject = ValidateSubject(_options.Subject);

            // 1. Configuration values take precedence when provided.
            var hasConfigPub = !string.IsNullOrWhiteSpace(_options.PublicKey);
            var hasConfigPriv = !string.IsNullOrWhiteSpace(_options.PrivateKey);
            if (hasConfigPub || hasConfigPriv)
            {
                if (!hasConfigPub || !hasConfigPriv)
                {
                    throw new InvalidOperationException(
                        "Både Push:Vapid:PublicKey och Push:Vapid:PrivateKey måste anges om VAPID-nycklar konfigureras manuellt.");
                }

                var validated = ValidateKeyPair(_options.PublicKey!.Trim(), _options.PrivateKey!.Trim(), subject, "konfigurationen");
                _cached = validated;
                _logger.LogInformation("Laddade VAPID-nycklar från konfiguration.");
                return validated;
            }

            // 2. Existing key file on persistent volume.
            var filePath = _options.KeysFilePath;
            if (string.IsNullOrWhiteSpace(filePath))
                throw new InvalidOperationException("Sökväg för VAPID-nyckelfil (KeysFilePath) får inte vara tom.");

            if (File.Exists(filePath))
            {
                VapidFileModel? model;
                try
                {
                    var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                    model = JsonSerializer.Deserialize<VapidFileModel>(json, JsonOptions);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Befintlig VAPID-nyckelfil '{filePath}' kunde inte läsas eller tolkas. Åtgärda filen manuellt i stället för att skriva över den.",
                        ex);
                }

                if (model == null ||
                    string.IsNullOrWhiteSpace(model.PublicKey) ||
                    string.IsNullOrWhiteSpace(model.PrivateKey))
                {
                    throw new InvalidOperationException(
                        $"Befintlig VAPID-nyckelfil '{filePath}' saknar giltig publik eller privat nyckel.");
                }

                var fileSubject = string.IsNullOrWhiteSpace(model.Subject) ? subject : ValidateSubject(model.Subject);
                var validated = ValidateKeyPair(model.PublicKey.Trim(), model.PrivateKey.Trim(), fileSubject, filePath);
                _cached = validated;
                _logger.LogInformation("Laddade befintliga VAPID-nycklar från {KeysFilePath}.", filePath);
                return validated;
            }

            // 3. Generate new P-256 keypair and persist atomically.
            var generated = GenerateKeyPair(subject);
            await WriteAtomicallyAsync(filePath, generated, cancellationToken);
            _cached = generated;
            _logger.LogInformation("Genererade och sparade nya VAPID-nycklar till {KeysFilePath}.", filePath);
            return generated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static VapidKeyPair GenerateKeyPair(string subject)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);

        if (parameters.Q.X is not { Length: 32 } ||
            parameters.Q.Y is not { Length: 32 } ||
            parameters.D is not { Length: 32 })
        {
            throw new InvalidOperationException("Genererad P-256-nyckel har oväntad längd.");
        }

        var publicBytes = new byte[65];
        publicBytes[0] = 0x04;
        Buffer.BlockCopy(parameters.Q.X, 0, publicBytes, 1, 32);
        Buffer.BlockCopy(parameters.Q.Y, 0, publicBytes, 33, 32);

        return new VapidKeyPair(
            Base64Url.EncodeToString(publicBytes),
            Base64Url.EncodeToString(parameters.D),
            subject);
    }

    private static VapidKeyPair ValidateKeyPair(string publicKeyB64Url, string privateKeyB64Url, string subject, string source)
    {
        byte[] pubBytes;
        byte[] privBytes;
        try
        {
            pubBytes = Base64Url.DecodeFromChars(publicKeyB64Url);
            privBytes = Base64Url.DecodeFromChars(privateKeyB64Url);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Ogiltig Base64Url-kodning i VAPID-nycklar från {source}.", ex);
        }

        if (pubBytes.Length != 65 || pubBytes[0] != 0x04 || privBytes.Length != 32)
        {
            throw new InvalidOperationException($"VAPID-nycklar från {source} har ogiltig P-256-längd.");
        }

        try
        {
            var ecParams = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = pubBytes[1..33],
                    Y = pubBytes[33..65]
                },
                D = privBytes
            };

            using var ecdsa = ECDsa.Create(ecParams);
            ecdsa.ExportParameters(includePrivateParameters: false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"VAPID-nyckelparet från {source} är ogiltigt eller matchar inte P-256.", ex);
        }

        return new VapidKeyPair(publicKeyB64Url, privateKeyB64Url, subject);
    }

    private static string ValidateSubject(string? subject)
    {
        var value = string.IsNullOrWhiteSpace(subject) ? VapidOptions.DefaultSubject : subject.Trim();
        if (!value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("VAPID Subject måste börja med 'mailto:' eller 'https://'.");
        }

        return value;
    }

    private static async Task WriteAtomicallyAsync(string filePath, VapidKeyPair keys, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var model = new VapidFileModel
        {
            PublicKey = keys.PublicKeyBase64Url,
            PrivateKey = keys.PrivateKeyBase64Url,
            Subject = keys.Subject
        };

        var json = JsonSerializer.Serialize(model, JsonOptions);
        var tempPath = $"{filePath}.tmp.{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tempPath, filePath, overwrite: false);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup
            }
            throw;
        }
    }

    private sealed class VapidFileModel
    {
        public string PublicKey { get; set; } = string.Empty;
        public string PrivateKey { get; set; } = string.Empty;
        public string? Subject { get; set; }
    }
}
