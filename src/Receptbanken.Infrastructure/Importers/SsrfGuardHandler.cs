using System.Net;
using System.Net.Sockets;

namespace Receptbanken.Infrastructure.Importers;

public static class SsrfValidator
{
    private static readonly HashSet<string> BlockedHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "localhost.localdomain",
        "ip6-localhost",
        "ip6-loopback",
        "metadata.google.internal",
        "metadata"
    };

    public static bool IsSafeUri(Uri? uri, out string? errorReason)
    {
        errorReason = null;

        if (uri == null || !uri.IsAbsoluteUri)
        {
            errorReason = "Ogiltig webbadress. Ange en fullständig URL som börjar med http:// eller https://.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            errorReason = $"Protokollet '{uri.Scheme}' stöds inte. Endast http och https är tillåtna.";
            return false;
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host) || BlockedHostnames.Contains(host.TrimEnd('.')))
        {
            errorReason = "Lokala eller interna värdnamn är blockerade av säkerhetsskäl.";
            return false;
        }

        if (IPAddress.TryParse(host, out var literalIp))
        {
            if (!IsPublicIpAddress(literalIp))
            {
                errorReason = "Privata, lokala och reserverade IP-adresser är blockerade av säkerhetsskäl.";
                return false;
            }
        }

        return true;
    }

    public static bool IsPublicIpAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
            return false;

        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            byte b0 = bytes[0];
            byte b1 = bytes[1];

            // 0.0.0.0/8 (Current network)
            if (b0 == 0) return false;

            // 10.0.0.0/8 (RFC 1918 Private)
            if (b0 == 10) return false;

            // 100.64.0.0/10 (Carrier-Grade NAT RFC 6598)
            if (b0 == 100 && (b1 >= 64 && b1 <= 127)) return false;

            // 127.0.0.0/8 (Loopback)
            if (b0 == 127) return false;

            // 169.254.0.0/16 (Link-local & Cloud metadata 169.254.169.254)
            if (b0 == 169 && b1 == 254) return false;

            // 172.16.0.0/12 (RFC 1918 Private)
            if (b0 == 172 && (b1 >= 16 && b1 <= 31)) return false;

            // 192.0.0.0/24 & 192.0.2.0/24 (IETF & TEST-NET-1)
            if (b0 == 192 && b1 == 0 && (bytes[2] == 0 || bytes[2] == 2)) return false;

            // 192.168.0.0/16 (RFC 1918 Private)
            if (b0 == 192 && b1 == 168) return false;

            // 198.18.0.0/15 (Benchmarking)
            if (b0 == 198 && (b1 == 18 || b1 == 19)) return false;

            // 198.51.100.0/24 & 203.0.113.0/24 (TEST-NET-2 & TEST-NET-3)
            if (b0 == 198 && b1 == 51 && bytes[2] == 100) return false;
            if (b0 == 203 && b1 == 0 && bytes[2] == 113) return false;

            // 224.0.0.0/4 (Multicast) & 240.0.0.0/4 (Reserved)
            if (b0 >= 224) return false;

            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return false;

            var bytes = ip.GetAddressBytes();
            // Unique Local Addresses fc00::/7 (fc00..fdff)
            if ((bytes[0] & 0xFE) == 0xFC)
                return false;

            // 2001:db8::/32 (Documentation)
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
                return false;

            return true;
        }

        return false;
    }

    public static SocketsHttpHandler CreateSafeSocketsHttpHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                if (BlockedHostnames.Contains(host.TrimEnd('.')))
                {
                    throw new InvalidOperationException("Anslutning till lokalt värdnamn blockerades av SSRF-skyddet.");
                }

                IPAddress[] addresses;
                if (IPAddress.TryParse(host, out var parsedIp))
                {
                    addresses = [parsedIp];
                }
                else
                {
                    addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                }

                if (addresses.Length == 0 || addresses.Any(a => !IsPublicIpAddress(a)))
                {
                    throw new InvalidOperationException("Anslutning till privat eller intern IP-adress blockerades av SSRF-skyddet.");
                }

                // Prefer IPv4 first for maximum compatibility in Docker containers without IPv6 routing,
                // then fall back to remaining validated public addresses.
                var orderedAddresses = addresses
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();

                Exception? lastException = null;
                foreach (var targetIp in orderedAddresses)
                {
                    var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        socket.Dispose();
                        lastException = ex;
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                throw lastException ?? new SocketException((int)SocketError.HostUnreachable);
            }
        };
    }
}
