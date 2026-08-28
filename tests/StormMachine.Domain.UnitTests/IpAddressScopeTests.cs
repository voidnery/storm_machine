using System.Net;
using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки области действия адреса IPv6.
/// </summary>
/// <remarks>
/// Тест закрепляет ошибку, которая уже была допущена: первая версия проверки готовности
/// к IPv6 считала глобальным всё, что не локально для канала, — и объявляла машину
/// имеющей глобальный адрес на основании петлевого <c>::1</c>, который есть всегда.
/// Инструмент сообщал «адрес есть, связности нет» вместо честного «адреса нет»,
/// то есть указывал не на того виновника.
/// </remarks>
public sealed class IpAddressScopeTests
{
    [Theory]
    [InlineData("::1", Ipv6Scope.Loopback)]
    [InlineData("::", Ipv6Scope.Loopback)]
    [InlineData("fe80::1c2d:3e4f:5a6b:7c8d", Ipv6Scope.LinkLocal)]
    [InlineData("fd00::1", Ipv6Scope.UniqueLocal)]
    [InlineData("fc00::1", Ipv6Scope.UniqueLocal)]
    [InlineData("fec0::1", Ipv6Scope.UniqueLocal)]
    [InlineData("ff02::1", Ipv6Scope.Multicast)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", Ipv6Scope.Tunnelled)]
    [InlineData("2002:c0a8:0001::1", Ipv6Scope.Tunnelled)]
    [InlineData("2a06:98c1:3122:8000::", Ipv6Scope.Global)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", Ipv6Scope.Global)]
    public void ClassifyV6_NamesTheScope(string address, Ipv6Scope expected) =>
        Assert.Equal(expected, IpAddressScope.ClassifyV6(IPAddress.Parse(address)));

    [Fact]
    public void Ipv4_IsNotIpv6() =>
        Assert.Equal(Ipv6Scope.NotIpv6, IpAddressScope.ClassifyV6(IPAddress.Parse("192.168.1.1")));

    [Fact]
    public void Null_IsNotIpv6() => Assert.Equal(Ipv6Scope.NotIpv6, IpAddressScope.ClassifyV6(null));

    [Fact]
    public void OnlyGlobalIsRoutable()
    {
        Assert.True(IpAddressScope.IsGloballyRoutableV6(IPAddress.Parse("2a06:98c1:3122:8000::")));
        Assert.False(IpAddressScope.IsGloballyRoutableV6(IPAddress.IPv6Loopback));
        Assert.False(IpAddressScope.IsGloballyRoutableV6(IPAddress.Parse("fd00::1")));
    }

    [Fact]
    public void TunnelIsNamedSeparately_NotFoldedIntoGlobal()
    {
        // Teredo даёт связность, но поверх IPv4 и через чужой ретранслятор: задержка
        // у него своя, и «готовность к IPv6» на туннеле — другой ответ.
        var teredo = IPAddress.Parse("2001:0:4136:e378:8000:63bf:3fff:fdd2");

        Assert.False(IpAddressScope.IsGloballyRoutableV6(teredo));
        Assert.Contains("туннель", IpAddressScope.Describe(Ipv6Scope.Tunnelled), StringComparison.Ordinal);
    }
}
