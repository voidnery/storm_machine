using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки того, что можно сказать об устройстве по самому его MAC-адресу.
/// </summary>
/// <remarks>
/// Реестр IEEE отвечает на вопрос «кто выпустил», но у части адресов производителя
/// нет вовсе. Строка «ICANN, IANA Department» напротив шлюза формально верна
/// и совершенно бесполезна: на деле это виртуальный адрес VRRP, и последний байт —
/// номер группы резервирования. Именно этот факт ищут, когда смотрят на шлюз.
/// </remarks>
public sealed class MacAddressesTests
{
    [Theory]
    [InlineData("00-00-5E-00-01-C8", "VRRP", "200")]
    [InlineData("00-00-5E-00-01-01", "VRRP", "1")]
    [InlineData("00:00:5e:00:02:0a", "IPv6", "10")]
    [InlineData("00-00-0C-07-AC-0B", "HSRP", "11")]
    [InlineData("00-00-0C-9F-F0-64", "HSRP v2", "100")]
    [InlineData("00-07-B4-00-05-01", "GLBP", "5.1")]
    public void RedundancyProtocols_AreNamed(string mac, string protocol, string group)
    {
        var description = MacAddresses.DescribeVirtual(mac);

        Assert.NotNull(description);
        Assert.Contains(protocol, description, StringComparison.Ordinal);
        Assert.Contains(group, description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00-15-5D-C8-B1-09")]
    [InlineData("D8-43-AE-5F-BF-B4")]
    [InlineData("02-00-00-00-00-01")]
    [InlineData("00-00-5E-00-03-01")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не адрес")]
    public void OrdinaryAddresses_AreNotVirtual(string? mac) =>
        Assert.Null(MacAddresses.DescribeVirtual(mac));

    [Fact]
    public void VirtualAddress_OutranksTheRegistryVendor()
    {
        // Реестр относит этот блок к IANA, и это правда. Но правда не о том:
        // производителя у виртуального адреса нет, а есть протокол и номер группы.
        var description = MacAddresses.Describe("00-00-5E-00-01-C8", "ICANN, IANA Department");

        Assert.Contains("VRRP", description, StringComparison.Ordinal);
        Assert.DoesNotContain("IANA", description, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualLabels_FitATableColumn()
    {
        // Подписи живут в столбце рядом с именами вендоров. Длинная формулировка
        // обрезалась бы ровно на номере группы — на единственном, что несёт сведения.
        foreach (var mac in new[] { "00-00-5E-00-01-C8", "00-00-0C-07-AC-0B", "00-07-B4-00-05-01" })
        {
            var description = MacAddresses.DescribeVirtual(mac);

            Assert.NotNull(description);
            Assert.True(description.Length <= 26, $"«{description}» — {description.Length} знаков, столбец уже.");
        }
    }

    [Fact]
    public void KnownVendor_IsShownAsIs() =>
        Assert.Equal("Synology Incorporated", MacAddresses.Describe("00-11-32-E4-70-AA", "Synology Incorporated"));

    [Fact]
    public void LocalAddress_IsExplainedRatherThanDashed()
    {
        // Прочерк читался бы как «не нашли в базе», хотя искать там нечего:
        // так выглядят случайные адреса телефонов и виртуальные адаптеры.
        Assert.Equal("локальный MAC", MacAddresses.Describe("2E-AF-19-F1-AF-E1", vendor: null));
    }

    [Fact]
    public void UnknownUniversalAddress_IsDashed() =>
        Assert.Equal("—", MacAddresses.Describe("00-15-5D-C8-B1-09", vendor: null));

    [Theory]
    [InlineData("02-00-00-00-00-01", true)]
    [InlineData("AE-73-1E-DB-0C-40", true)]
    [InlineData("00-15-5D-C8-B1-09", false)]
    [InlineData("D8:43:AE:5F:BF:B4", false)]
    [InlineData("0015.5dc8.b109", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void LocallyAdministered_IsRecognisedInAnyWriting(string? mac, bool expected) =>
        Assert.Equal(expected, MacAddresses.IsLocallyAdministered(mac));
}
