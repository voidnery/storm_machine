using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;
using StormMachine.Domain.Topology;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Карта, когда есть SNMP.
/// </summary>
/// <remarks>
/// Ради этого и делался уровень 1. Без опроса оборудования карта отвечает
/// «эти узлы в одном широковещательном домене» — утверждение верное, но слабое.
/// С опросом она отвечает «это устройство воткнуто вот в этот порт вот этого
/// коммутатора», и разница между догадкой и фактом обязана быть видна на самой карте,
/// а не только в голове у того, кто её строил.
/// </remarks>
public sealed class TopologySwitchTests
{
    private static readonly DateTimeOffset Observed = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static LocalSubnet Subnet() => new()
    {
        Cidr = "192.168.1.0/24",
        InterfaceName = "тестовый",
        InterfaceAddress = "192.168.1.100",
    };

    private static Device Host(string address, string mac) => Domain.Discovery.Device.FromEvidence(
        address,
        [
            Evidence.Of(EvidenceSource.IcmpEcho, EvidenceKind.Alive, "да", Observed),
            Evidence.Of(EvidenceSource.ArpTable, EvidenceKind.MacAddress, mac, Observed),
        ],
        Observed,
        Observed,
        isOnline: true);

    private static SnmpInterface Port(int index, string name) => new()
    {
        Index = index,
        Name = name,
        Type = SnmpInterface.EthernetType,
        SpeedBitsPerSecond = 1_000_000_000,
        AdminStatus = InterfaceStatus.Up,
        OperStatus = InterfaceStatus.Up,
    };

    private static SnmpDevice Switch(
        string address = "192.168.1.2",
        string name = "sw-access-01",
        IReadOnlyList<ForwardingEntry>? forwarding = null,
        IReadOnlyList<SnmpNeighbor>? neighbors = null) => new()
    {
        Address = address,
        System = new SnmpSystem { Description = "Test switch, 8 ports", Name = name, Services = 2 },
        ObservedUtc = Observed,
        Interfaces = [Port(1, "Gi0/1"), Port(2, "Gi0/2"), Port(3, "Gi0/3")],
        Forwarding = forwarding ?? [],
        Neighbors = neighbors ?? [],
    };

    private static ForwardingEntry Entry(string mac, int ifIndex, string port) => new()
    {
        MacAddress = mac,
        BridgePort = ifIndex,
        IfIndex = ifIndex,
        PortName = port,
        IsLearned = true,
    };

    // ---------------------------------------------------------------- порт вместо догадки

    [Fact(DisplayName = "Устройство цепляется к порту коммутатора, а не к подсети")]
    public void DeviceHangsOnSwitchPort()
    {
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = [Host("192.168.1.10", "AA-BB-CC-00-00-01")],
            Switches = [Switch(forwarding: [Entry("AA-BB-CC-00-00-01", 2, "Gi0/2")])],
        });

        var link = graph.Links.Single(l => l.To == "AA-BB-CC-00-00-01" || l.From == "AA-BB-CC-00-00-01");

        Assert.Equal("свитч:192.168.1.2", link.From);
        Assert.Equal(LinkConfidence.Confirmed, link.Confidence);
        Assert.Contains("Gi0/2", link.Because, StringComparison.Ordinal);
        Assert.Contains("BRIDGE-MIB", link.Because, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Порт с несколькими адресами устройства к себе не притягивает")]
    public void UplinkPortDoesNotClaimDevices()
    {
        // За таким портом стоит ещё один коммутатор, а не четыре компьютера.
        // Нарисовать их воткнутыми в него значило бы соврать уверенно.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = [Host("192.168.1.10", "AA-BB-CC-00-00-01")],
            Switches =
            [
                Switch(forwarding:
                [
                    Entry("AA-BB-CC-00-00-01", 1, "Gi0/1"),
                    Entry("AA-BB-CC-00-00-02", 1, "Gi0/1"),
                    Entry("AA-BB-CC-00-00-03", 1, "Gi0/1"),
                ]),
            ],
        });

        var link = graph.Links.Single(l => l.To == "AA-BB-CC-00-00-01");

        Assert.Equal("сеть:192.168.1.0/24", link.From);
        Assert.DoesNotContain("BRIDGE-MIB", link.Because, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Порт с объявленным соседом устройства к себе не притягивает")]
    public void PortWithNeighborIsUplink()
    {
        // Даже если адрес на нём один: сосед объявился, значит это межкоммутаторное
        // соединение, и единственный выученный адрес — чужой транзит.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = [Host("192.168.1.10", "AA-BB-CC-00-00-01")],
            Switches =
            [
                Switch(
                    forwarding: [Entry("AA-BB-CC-00-00-01", 1, "Gi0/1")],
                    neighbors:
                    [
                        new SnmpNeighbor
                        {
                            Protocol = NeighborProtocol.Lldp,
                            LocalIfIndex = 1,
                            LocalPort = "Gi0/1",
                            RemoteName = "sw-core-01",
                            RemotePort = "Te1/0/1",
                        },
                    ]),
            ],
        });

        var link = graph.Links.Single(l => l.To == "AA-BB-CC-00-00-01");

        Assert.Equal("сеть:192.168.1.0/24", link.From);
    }

    // ---------------------------------------------------------------------- соседи

    [Fact(DisplayName = "Сосед по LLDP появляется на карте подтверждённой связью")]
    public void NeighborBecomesLink()
    {
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Switches =
            [
                Switch(neighbors:
                [
                    new SnmpNeighbor
                    {
                        Protocol = NeighborProtocol.Lldp,
                        LocalIfIndex = 1,
                        LocalPort = "Gi0/1",
                        RemoteName = "sw-core-01",
                        RemotePort = "Te1/0/24",
                    },
                ]),
            ],
        });

        var link = graph.Links.Single(l => l.To.Contains("sw-core-01", StringComparison.Ordinal));

        Assert.Equal(LinkConfidence.Confirmed, link.Confidence);
        Assert.Contains("LLDP", link.Because, StringComparison.Ordinal);
        Assert.Contains("Te1/0/24", link.Because, StringComparison.Ordinal);

        // Оговорка обязательна: между двумя объявившимися соседями может стоять
        // неуправляемый коммутатор.
        Assert.Contains("неуправляемый", link.Because, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Необъявленный сосед помечается неопрошенным")]
    public void UnpolledNeighborIsMarked()
    {
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Switches =
            [
                Switch(neighbors:
                [
                    new SnmpNeighbor
                    {
                        Protocol = NeighborProtocol.Lldp,
                        LocalIfIndex = 1,
                        RemoteName = "sw-core-01",
                    },
                ]),
            ],
        });

        var node = graph.Nodes.Single(n => n.Label == "sw-core-01");

        Assert.Equal(TopologyNodeKind.Switch, node.Kind);
        Assert.Contains("сам не опрошен", node.Detail, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Два опрошенных соседа соединяются, а не двоятся")]
    public void KnownNeighborsAreJoined()
    {
        var access = Switch(
            address: "192.168.1.2",
            name: "sw-access-01",
            neighbors:
            [
                new SnmpNeighbor
                {
                    Protocol = NeighborProtocol.Lldp,
                    LocalIfIndex = 1,
                    RemoteName = "sw-core-01",
                    RemotePort = "Te1/0/24",
                },
            ]);

        var core = Switch(address: "192.168.1.3", name: "sw-core-01");

        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Switches = [access, core],
        });

        Assert.Single(graph.Nodes, n => n.Label == "sw-core-01");
        Assert.Contains(graph.Links, l =>
            l.From == "свитч:192.168.1.2" && l.To == "свитч:192.168.1.3");
    }

    // ---------------------------------------------------------------- деградация

    [Fact(DisplayName = "Без SNMP карта строится по-прежнему и не притворяется точнее")]
    public void WithoutSnmpNothingChanges()
    {
        // Условие приёмки И-17: при отсутствии SNMP продукт деградирует явно,
        // без ложной уверенности.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = [Host("192.168.1.10", "AA-BB-CC-00-00-01")],
        });

        var link = graph.Links.Single(l => l.To == "AA-BB-CC-00-00-01");

        Assert.Equal("сеть:192.168.1.0/24", link.From);
        Assert.Equal(LinkConfidence.Confirmed, link.Confidence);
        Assert.Contains("ARP", link.Because, StringComparison.Ordinal);
        Assert.DoesNotContain(graph.Nodes, n => n.Kind == TopologyNodeKind.Switch);
    }

    [Fact(DisplayName = "Коммутатор с адресом в подсети связан с ней подтверждённо")]
    public void SwitchJoinsItsSubnet()
    {
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Switches = [Switch()],
        });

        var link = graph.Links.Single(l =>
            l.From == "сеть:192.168.1.0/24" && l.To == "свитч:192.168.1.2");

        Assert.Equal(LinkConfidence.Confirmed, link.Confidence);
    }

    [Fact(DisplayName = "Карта не зависит от порядка опрошенных устройств")]
    public void OrderDoesNotMatter()
    {
        // То же требование, что и ко всей карте: пересчёт обязан давать одно и то же,
        // иначе «что изменилось» покажет перестановку вместо изменений.
        var first = Switch(address: "192.168.1.2", name: "sw-a");
        var second = Switch(address: "192.168.1.3", name: "sw-b");

        var direct = TopologyGraph.Build(new TopologyInput { Subnets = [Subnet()], Switches = [first, second] });
        var reversed = TopologyGraph.Build(new TopologyInput { Subnets = [Subnet()], Switches = [second, first] });

        Assert.Equal(
            direct.Links.Select(l => $"{l.From}->{l.To}"),
            reversed.Links.Select(l => $"{l.From}->{l.To}"));
    }
}
