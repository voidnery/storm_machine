using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;
using StormMachine.Domain.Topology;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Карта и VLAN.
/// </summary>
/// <remarks>
/// Долг И-17, закрытый в И-23. Номер VLAN читался из таблицы пересылки, показывался
/// в выводе SNMP и терялся на карте: два устройства в разных VLAN на одном коммутаторе
/// выглядели соседями.
/// <para>
/// Это не пробел показа, а <b>неверное утверждение</b>. Разные VLAN — разные
/// широковещательные домены; устройства в них друг друга не видят, а карта, на которой
/// они висят рядом на одном узле, говорит обратное. Вся ценность карты в том, что
/// догадка на ней не выглядит фактом, — и здесь фактом выглядело то, чего нет вовсе.
/// </para>
/// </remarks>
public sealed class TopologyVlanTests
{
    private static readonly DateTimeOffset Moment = DateTimeOffset.UnixEpoch;

    private static Device Host(string address, string mac) => new()
    {
        Address = address,
        Addresses = [address],
        MacAddress = mac,
        FirstSeenUtc = Moment,
        LastSeenUtc = Moment,
        IsOnline = true,
    };

    private static SnmpDevice Switch(params (int Port, string Mac, int? Vlan)[] wired) => new()
    {
        Address = "10.0.0.1",
        System = new SnmpSystem { Description = "коммутатор", Name = "sw-1" },
        ObservedUtc = Moment,
        Interfaces =
        [
            .. wired.Select(w => new SnmpInterface
            {
                Index = w.Port,
                Name = $"Gi0/{w.Port}",
                Type = SnmpInterface.EthernetType,
                AdminStatus = InterfaceStatus.Up,
                OperStatus = InterfaceStatus.Up,
            }),
        ],
        Forwarding =
        [
            .. wired.Select(w => new ForwardingEntry
            {
                MacAddress = w.Mac,
                BridgePort = w.Port,
                IfIndex = w.Port,
                Vlan = w.Vlan,
                IsLearned = true,
            }),
        ],
    };

    private static TopologyInput Input(SnmpDevice sw, params Device[] devices) => new()
    {
        Devices = devices,
        Switches = [sw],
        Subnets =
        [
            new LocalSubnet
            {
                Cidr = "10.0.0.0/24",
                InterfaceName = "Ethernet",
                InterfaceAddress = "10.0.0.2",
            },
        ],
    };

    /// <summary>
    /// Устройства в разных VLAN на одном коммутаторе — карта говорит, что они не соседи.
    /// </summary>
    /// <remarks>
    /// Главное утверждение файла. Связи при этом остаются верными: устройства
    /// действительно воткнуты в этот коммутатор. Неверно было бы прочесть их как
    /// соседство — и вот об этом карта обязана сказать словами.
    /// </remarks>
    [Fact]
    public void DevicesInDifferentVlans_AreNotClaimedAsNeighbours()
    {
        var graph = TopologyGraph.Build(Input(
            Switch((1, "00:11:22:33:44:01", 10), (2, "00:11:22:33:44:02", 20)),
            Host("10.0.0.11", "00:11:22:33:44:01"),
            Host("10.0.0.12", "00:11:22:33:44:02")));

        var caveat = Assert.Single(graph.Caveats);

        Assert.Contains("разных VLAN", caveat, StringComparison.Ordinal);
        Assert.Contains("10", caveat, StringComparison.Ordinal);
        Assert.Contains("20", caveat, StringComparison.Ordinal);
        Assert.Contains("соседями не являются", caveat, StringComparison.Ordinal);
    }

    /// <summary>Одна VLAN на всех — оговаривать нечего.</summary>
    [Fact]
    public void DevicesInOneVlan_NeedNoCaveat()
    {
        var graph = TopologyGraph.Build(Input(
            Switch((1, "00:11:22:33:44:01", 10), (2, "00:11:22:33:44:02", 10)),
            Host("10.0.0.11", "00:11:22:33:44:01"),
            Host("10.0.0.12", "00:11:22:33:44:02")));

        Assert.Empty(graph.Caveats);
    }

    /// <summary>
    /// Без Q-BRIDGE-MIB номеров нет, и продукт молчит.
    /// </summary>
    /// <remarks>
    /// Предупреждать о том, чего не наблюдали, — тот же вид вранья, что и молчать
    /// о наблюдённом. Коммутатор, отдающий таблицу без разбивки по VLAN, ничего
    /// о доменах не сообщил, и придумывать за него нельзя.
    /// </remarks>
    [Fact]
    public void WithoutVlanNumbers_TheMapStaysSilent()
    {
        var graph = TopologyGraph.Build(Input(
            Switch((1, "00:11:22:33:44:01", null), (2, "00:11:22:33:44:02", null)),
            Host("10.0.0.11", "00:11:22:33:44:01"),
            Host("10.0.0.12", "00:11:22:33:44:02")));

        Assert.Empty(graph.Caveats);
    }

    /// <summary>Номер VLAN доходит до узла и до причины связи.</summary>
    [Fact]
    public void VlanReachesTheNodeAndTheReason()
    {
        var graph = TopologyGraph.Build(Input(
            Switch((1, "00:11:22:33:44:01", 42)),
            Host("10.0.0.11", "00:11:22:33:44:01")));

        var host = Assert.Single(graph.Nodes, n => n.Kind == TopologyNodeKind.Host);

        Assert.Equal(42, host.Vlan);

        var link = Assert.Single(graph.Links, l => l.To == host.Id);

        Assert.Contains("VLAN 42", link.Because, StringComparison.Ordinal);
    }

    /// <summary>
    /// Неизвестная VLAN и первая VLAN — не одно и то же.
    /// </summary>
    /// <remarks>
    /// Свести отсутствие сведений к «VLAN 1» значило бы выдумать наблюдение.
    /// Разница видна по тому, что у узла номер пуст, а не равен единице.
    /// </remarks>
    [Fact]
    public void UnknownVlan_IsNotVlanOne()
    {
        var graph = TopologyGraph.Build(Input(
            Switch((1, "00:11:22:33:44:01", null)),
            Host("10.0.0.11", "00:11:22:33:44:01")));

        var host = Assert.Single(graph.Nodes, n => n.Kind == TopologyNodeKind.Host);

        Assert.Null(host.Vlan);
    }

    /// <summary>Карта остаётся детерминированной: оговорки считаются одинаково.</summary>
    [Fact]
    public void CaveatsAreDeterministic()
    {
        var input = Input(
            Switch((1, "00:11:22:33:44:01", 10), (2, "00:11:22:33:44:02", 20), (3, "00:11:22:33:44:03", 30)),
            Host("10.0.0.11", "00:11:22:33:44:01"),
            Host("10.0.0.12", "00:11:22:33:44:02"),
            Host("10.0.0.13", "00:11:22:33:44:03"));

        Assert.Equal(TopologyGraph.Build(input).Caveats, TopologyGraph.Build(input).Caveats);
    }
}
