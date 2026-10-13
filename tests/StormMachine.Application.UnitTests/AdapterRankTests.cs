using StormMachine.Application.Abstractions;
using StormMachine.Application.Adapters;
using StormMachine.Domain.Measurements;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Кто из адаптеров «настоящий».
/// </summary>
/// <remarks>
/// Список интерфейсов Windows шире списка карт, и часть записей в нём появляется
/// временами — например, служебный фильтр NDIS, пока загружен драйвер захвата.
/// Он носит MAC настоящей карты, и без порядка предпочтения продукт показывал
/// оператору его имя вместо имени карты. Поймано боевым прогоном, И-25.
/// </remarks>
public sealed class AdapterRankTests
{
    [Fact(DisplayName = "Карта с маршрутом важнее служебного фильтра с тем же MAC")]
    public void Routed_BeatsFilter()
    {
        var filter = Adapter("Ethernet 4-WFP Native MAC Layer LightWeight Filter-0000", AdapterKind.Physical);
        var card = Adapter("Ethernet", AdapterKind.Physical, address: "192.168.200.10", gateway: "192.168.200.1");

        Assert.Equal("Ethernet", AdapterRank.MostRealFirst([filter, card]).First().Name);
    }

    [Fact(DisplayName = "При прочих равных физическая карта важнее виртуального коммутатора")]
    public void Physical_BeatsVirtual()
    {
        var virtualSwitch = Adapter("vEthernet (DefaultVirtLan)", AdapterKind.Virtual, address: "192.168.200.10");
        var card = Adapter("Ethernet", AdapterKind.Physical, address: "192.168.200.11");

        Assert.Equal("Ethernet", AdapterRank.MostRealFirst([virtualSwitch, card]).First().Name);
    }

    [Fact(DisplayName = "Виртуальный коммутатор с маршрутом важнее физической карты без адреса")]
    public void Routed_BeatsAddressless()
    {
        // На хосте с Hyper-V физическая карта отдана коммутатору и своего адреса
        // не имеет: трафик ходит через vEthernet, и спрашивать надо про него.
        var card = Adapter("Ethernet", AdapterKind.Physical);
        var virtualSwitch = Adapter(
            "vEthernet (DefaultVirtLan)",
            AdapterKind.Virtual,
            address: "192.168.200.10",
            gateway: "192.168.200.1");

        Assert.Equal("vEthernet (DefaultVirtLan)", AdapterRank.MostRealFirst([card, virtualSwitch]).First().Name);
    }

    [Fact(DisplayName = "Порядок внутри ступени не меняется")]
    public void WithinRank_OrderIsKept()
    {
        // Устойчивость важна: иначе список адаптеров переставлялся бы между
        // открытиями экрана, и оператор каждый раз искал бы свою карту заново.
        var first = Adapter("Первый", AdapterKind.Virtual);
        var second = Adapter("Второй", AdapterKind.Virtual);

        Assert.Equal(["Первый", "Второй"], AdapterRank.MostRealFirst([first, second]).Select(a => a.Name));
    }

    private static NetworkAdapter Adapter(
        string name,
        AdapterKind kind,
        string? address = null,
        string? gateway = null) => new()
    {
        Id = name,
        Name = name,
        Description = name,
        Kind = kind,
        IPv4Address = address,
        Gateways = gateway is null ? [] : [gateway],
        IsUp = true,
    };
}
