using System.Net;
using FluentAssertions;
using Receptbanken.Infrastructure.Importers;

namespace Receptbanken.Tests.Infrastructure;

public class SsrfValidatorTests
{
    [Theory]
    [InlineData("http://localhost/recept")]
    [InlineData("https://localhost:5000/recept")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://127.0.0.53/dns")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.0.0.1/internal")]
    [InlineData("http://172.16.0.1/router")]
    [InlineData("http://172.31.255.254/internal")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.64.0.1/cgnat")]
    [InlineData("http://[::1]/loopback")]
    [InlineData("http://[::ffff:127.0.0.1]/mapped-loopback")]
    [InlineData("http://[::ffff:192.168.1.50]/mapped-private")]
    [InlineData("http://[fe80::1]/link-local")]
    [InlineData("http://[fd00::1]/unique-local")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/recipe.txt")]
    [InlineData("javascript:alert(1)")]
    public void IsSafeUri_UnsafeOrInternalTargets_ShouldReturnFalse(string rawUrl)
    {
        Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri);
        var isSafe = SsrfValidator.IsSafeUri(uri, out var errorReason);

        isSafe.Should().BeFalse($"URL '{rawUrl}' must be blocked by SSRF guard");
        errorReason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("https://www.ica.se/recept/pannkakor-713191/")]
    [InlineData("https://www.arla.se/recept/kladdkaka/")]
    [InlineData("https://koket.se/klassiska-kottbullar")]
    [InlineData("https://93.184.216.34/recipe")]
    public void IsSafeUri_PublicHttpAndHttpsTargets_ShouldReturnTrue(string rawUrl)
    {
        var uri = new Uri(rawUrl);
        var isSafe = SsrfValidator.IsSafeUri(uri, out var errorReason);

        isSafe.Should().BeTrue();
        errorReason.Should().BeNull();
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.5", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("192.168.0.10", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    public void IsPublicIpAddress_ShouldClassifyAddressesCorrectly(string ipString, bool expectedPublic)
    {
        var ip = IPAddress.Parse(ipString);
        SsrfValidator.IsPublicIpAddress(ip).Should().Be(expectedPublic);
    }
}

