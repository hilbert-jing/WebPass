using System.Net;
using WebPass.Web.Application.Networking;
using Xunit;

namespace WebPass.UnitTests.Networking;

public sealed class Ipv4CidrTests
{
    [Fact]
    public void Slash24_has_254_usable_addresses_and_excludes_endpoints()
    {
        var cidr = Ipv4Cidr.Parse("10.0.0.0/24");

        Assert.Equal(254, cidr.GetUsableAddressCount());
        Assert.False(cidr.ContainsUsable(IPAddress.Parse("10.0.0.0")));
        Assert.False(cidr.ContainsUsable(IPAddress.Parse("10.0.0.255")));
        Assert.True(cidr.ContainsUsable(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void Parse_normalizes_network_and_detects_overlaps()
    {
        var cidr = Ipv4Cidr.Parse("10.0.0.18/24");

        Assert.Equal(IPAddress.Parse("10.0.0.0"), cidr.NetworkAddress);
        Assert.True(cidr.Contains(IPAddress.Parse("10.0.0.255")));
        Assert.True(cidr.Overlaps(Ipv4Cidr.Parse("10.0.0.128/25")));
        Assert.False(cidr.Overlaps(Ipv4Cidr.Parse("10.0.1.0/24")));
    }

    [Theory]
    [InlineData("2001:db8::/64")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/24/1")]
    public void Parse_rejects_invalid_or_non_ipv4_cidr(string value)
    {
        Assert.Throws<ArgumentException>(() => Ipv4Cidr.Parse(value));
    }

    [Fact]
    public void Usable_enumeration_pages_without_including_network_or_broadcast()
    {
        var cidr = Ipv4Cidr.Parse("192.168.4.0/29");

        var addresses = cidr.EnumerateUsableAddresses(skip: 2, take: 3).Select(x => x.ToString());

        Assert.Equal(["192.168.4.3", "192.168.4.4", "192.168.4.5"], addresses);
    }
}
