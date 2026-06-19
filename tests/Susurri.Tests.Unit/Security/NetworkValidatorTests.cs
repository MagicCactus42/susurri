using System.Net;
using Shouldly;
using Susurri.Shared.Abstractions.Security;
using Xunit;

namespace Susurri.Tests.Unit.Security;

public class NetworkValidatorTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("203.0.114.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PubliclyRoutable_Addresses_Are_Allowed(string ip)
    {
        NetworkValidator.IsPubliclyRoutable(IPAddress.Parse(ip)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    public void PrivateAndReserved_V4_Addresses_Are_Blocked(string ip)
    {
        NetworkValidator.IsPubliclyRoutable(IPAddress.Parse(ip)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::")]
    public void LinkLocal_And_Ula_V6_Addresses_Are_Blocked(string ip)
    {
        NetworkValidator.IsPubliclyRoutable(IPAddress.Parse(ip)).ShouldBeFalse();
    }

    [Fact]
    public void Null_Address_Is_Blocked()
    {
        NetworkValidator.IsPubliclyRoutable(null).ShouldBeFalse();
    }

    [Fact]
    public void IPv4_Mapped_Private_V6_Is_Blocked()
    {
        NetworkValidator.IsPubliclyRoutable(IPAddress.Parse("::ffff:192.168.1.1")).ShouldBeFalse();
    }
}
