using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Importers;

namespace Receptbanken.Infrastructure.Push;

/// <summary>
/// Validates Web Push subscription endpoints against HTTPS-only, SSRF rules, and known push provider hosts.
/// </summary>
public static class PushEndpointValidator
{
    private static readonly string[] AllowedHostSuffixes =
    [
        ".push.apple.com",
        ".notify.windows.com",
        ".push.services.mozilla.com",
        ".googleapis.com"
    ];

    private static readonly HashSet<string> AllowedExactHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "web.push.apple.com"
    };

    public static bool IsValidPushEndpoint(string? endpoint, out Uri? uri, out string? errorReason)
    {
        uri = null;
        errorReason = null;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            errorReason = "Push-endpoint saknas.";
            return false;
        }

        if (endpoint.Length > PushSubscriptionEntity.MaxEndpointLength)
        {
            errorReason = "Push-endpoint är för lång.";
            return false;
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var parsed))
        {
            errorReason = "Push-endpoint är inte en giltig absolut URL.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errorReason = "Push-endpoint måste använda HTTPS.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            errorReason = "Push-endpoint får inte innehålla användaruppgifter.";
            return false;
        }

        if (!SsrfValidator.IsSafeUri(parsed, out var ssrfReason))
        {
            errorReason = ssrfReason ?? "Push-endpoint blockerades av SSRF-skyddet.";
            return false;
        }

        var host = parsed.DnsSafeHost.TrimEnd('.');
        if (!IsAllowedPushHost(host))
        {
            errorReason = $"Push-leverantören '{host}' är inte tillåten.";
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsAllowedPushHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (AllowedExactHosts.Contains(host))
            return true;

        foreach (var suffix in AllowedHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Redacts the secret token path of a push endpoint for safe logging.
    /// </summary>
    public static string RedactEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "(none)";

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Authority}/...";

        return "(invalid-uri)";
    }
}
