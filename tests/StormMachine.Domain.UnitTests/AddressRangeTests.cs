using System.Net;
using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки диапазона адресов для сканирования.
/// </summary>
/// <remarks>
/// Диапазон — единственное место, где ошибка приводит не к неверному числу на экране,
/// а к активному действию по чужой сети. Поэтому здесь проверяются границы, а не только
/// счастливый путь: опечатка в маске должна отклоняться, а не выполняться.
/// </remarks>
public sealed class AddressRangeTests
{
    private static readonly string[] FourAddresses =
        ["192.168.1.10", "192.168.1.11", "192.168.1.12", "192.168.1.13"];

    [Fact]
    public void Cidr24_ExcludesNetworkAndBroadcast()
    {
        var range = AddressRange.Parse("192.168.1.0/24");

        // 256 адресов минус адрес сети и широковещательный: опрашивать их незачем,
        // а широковещательный запрос — это уже другое действие.
        Assert.Equal(254, range.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), range.First);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), range.Last);
    }

    [Fact]
    public void Cidr31And32_KeepEveryAddress()
    {
        // В точка-точка сетях адреса сети и широковещательного не существует,
        // и выбрасывать из них по два адреса значило бы не проверить ничего.
        Assert.Equal(2, AddressRange.Parse("10.0.0.0/31").Count);
        Assert.Equal(1, AddressRange.Parse("10.0.0.5/32").Count);
    }

    [Fact]
    public void Cidr_NormalisesToNetworkAddress()
    {
        var range = AddressRange.Parse("192.168.1.77/24");

        Assert.Equal("192.168.1.0/24", range.Text);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), range.First);
    }

    [Fact]
    public void Span_IsParsedAndOrdered()
    {
        var range = AddressRange.Parse("192.168.1.40-192.168.1.10");

        Assert.Equal(31, range.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), range.First);
        Assert.Equal(IPAddress.Parse("192.168.1.40"), range.Last);
    }

    [Fact]
    public void SingleAddress_IsARangeOfOne()
    {
        var range = AddressRange.Parse("8.8.8.8");

        Assert.Equal(1, range.Count);
        Assert.Equal(range.First, range.Last);
    }

    [Fact]
    public void TooWideRange_IsRefused()
    {
        // Опечатка в маске — самая дорогая ошибка в этой команде: /8 это шестнадцать
        // миллионов адресов и часы работы по чужой сети.
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => AddressRange.Parse("10.0.0.0/8"));

        Assert.Contains("опечатка", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("не адрес")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1.0/маска")]
    [InlineData("::1/64")]
    public void Malformed_IsRefused(string text) =>
        Assert.ThrowsAny<Exception>(() => AddressRange.Parse(text));

    [Fact]
    public void Enumerate_YieldsEveryAddressOnce()
    {
        var range = AddressRange.Parse("192.168.1.10-192.168.1.13");
        var addresses = range.Enumerate().Select(a => a.ToString()).ToList();

        Assert.Equal(FourAddresses, addresses);
    }

    [Fact]
    public void Enumerate_StopsAtTheEndOfAddressSpace()
    {
        // Граничный случай перечисления: без явной остановки счётчик переполнился бы
        // и перечисление пошло бы с нуля.
        var range = AddressRange.Parse("255.255.255.254-255.255.255.255");

        Assert.Equal(2, range.Enumerate().Count());
    }

    [Fact]
    public void Contains_KnowsItsOwnBounds()
    {
        var range = AddressRange.Parse("192.168.1.0/24");

        Assert.True(range.Contains(IPAddress.Parse("192.168.1.1")));
        Assert.True(range.Contains(IPAddress.Parse("192.168.1.254")));
        Assert.False(range.Contains(IPAddress.Parse("192.168.1.0")));
        Assert.False(range.Contains(IPAddress.Parse("192.168.2.1")));
        Assert.False(range.Contains(IPAddress.Parse("::1")));
    }

    [Fact]
    public void FromInterface_TakesTheSubnetOfTheAddress()
    {
        var range = AddressRange.FromInterface(IPAddress.Parse("192.168.200.110"), 24);

        Assert.Equal("192.168.200.0/24", range.Text);
        Assert.True(range.Contains(IPAddress.Parse("192.168.200.110")));
    }

    [Fact]
    public void RoundTrips_ThroughItsOwnText()
    {
        // Хранилище записывает диапазон строкой и разбирает её обратно, когда нужно
        // понять, что именно проверялось. Расхождение здесь тихо испортило бы инвентарь.
        foreach (var text in new[] { "192.168.1.0/24", "10.0.0.0/30", "172.16.0.10-172.16.0.20", "8.8.8.8" })
        {
            var original = AddressRange.Parse(text);
            var restored = AddressRange.Parse(original.Text);

            Assert.Equal(original.Count, restored.Count);
            Assert.Equal(original.First, restored.First);
            Assert.Equal(original.Last, restored.Last);
        }
    }
}
