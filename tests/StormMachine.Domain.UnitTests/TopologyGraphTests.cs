using StormMachine.Domain.Discovery;
using StormMachine.Domain.Topology;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки построения карты сети.
/// </summary>
/// <remarks>
/// Два свойства закрепляются здесь, и оба существенны.
/// <list type="number">
/// <item><b>Детерминизм.</b> Одни и те же свидетельства обязаны давать одну и ту же
/// карту независимо от порядка поступления — иначе повторное сканирование меняло бы
/// её произвольно, и «что изменилось» показывало бы перестановку вместо изменений.</item>
/// <item><b>Видимая достоверность.</b> Карта, на которой догадка выглядит как факт,
/// хуже отсутствия карты: по ней принимают решения, не зная, что часть нарисованного
/// инструмент домыслил.</item>
/// </list>
/// </remarks>
public sealed class TopologyGraphTests
{
    private static readonly DateTimeOffset Observed = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] TwoDestinations = ["1.1.1.1", "8.8.8.8"];

    private static readonly string[] ThreeAddressesInOrder =
        ["192.168.1.10", "192.168.1.20", "192.168.1.30"];

    private static Device Device(string address, string? mac = null, string? name = null, bool viaArp = true)
    {
        var evidence = new List<Evidence>
        {
            Evidence.Of(EvidenceSource.IcmpEcho, EvidenceKind.Alive, "да", Observed),
        };

        if (mac is not null)
        {
            // Источник свидетельства о MAC решает: ответ на ARP доказывает общий
            // широковещательный домен, ответ на ICMP — нет.
            evidence.Add(Evidence.Of(
                viaArp ? EvidenceSource.ArpTable : EvidenceSource.Manual,
                EvidenceKind.MacAddress,
                mac,
                Observed));
        }

        if (name is not null)
        {
            evidence.Add(Evidence.Of(EvidenceSource.Netbios, EvidenceKind.HostName, name, Observed));
        }

        return Domain.Discovery.Device.FromEvidence(address, evidence, Observed, Observed, isOnline: true);
    }

    private static LocalSubnet Subnet(string cidr = "192.168.1.0/24", params string[] gateways) => new()
    {
        Cidr = cidr,
        InterfaceName = "тестовый",
        InterfaceAddress = "192.168.1.100",
        Gateways = gateways,
    };

    // ------------------------------------------------------------ основа карты

    [Fact]
    public void EmptyInput_GivesJustThisMachine()
    {
        var graph = TopologyGraph.Build(new TopologyInput());

        var node = Assert.Single(graph.Nodes);
        Assert.Equal(TopologyNodeKind.ThisMachine, node.Kind);
        Assert.Empty(graph.Links);
    }

    [Fact]
    public void SubnetIsAttachedToThisMachineAsFact()
    {
        var graph = TopologyGraph.Build(new TopologyInput { Subnets = [Subnet()] });

        var link = Assert.Single(graph.Links);

        // Что мы стоим в собственной сети — не вывод, а факт: у интерфейса там адрес.
        Assert.Equal(TopologyGraph.ThisMachineId, link.From);
        Assert.Equal(LinkConfidence.Confirmed, link.Confidence);
    }

    [Fact]
    public void GatewayBecomesRouterAndLeadsToInternet()
    {
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet("192.168.1.0/24", "192.168.1.1")],
            Devices = [Device("192.168.1.1", "AA-BB-CC-DD-EE-FF")],
        });

        Assert.Contains(graph.Nodes, n => n.Kind == TopologyNodeKind.Router);
        Assert.Contains(graph.Nodes, n => n.Kind == TopologyNodeKind.Internet);

        // Что шлюз ведёт наружу — вывод из его роли, а не наблюдение:
        // сеть без выхода в интернет существует.
        var outward = Assert.Single(graph.Links, l => l.To == TopologyGraph.InternetId);
        Assert.Equal(LinkConfidence.Inferred, outward.Confidence);
    }

    // ------------------------------------------------------------ достоверность

    [Fact]
    public void ArpAnswer_MakesMembershipAFact()
    {
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = [Device("192.168.1.50", "AA-BB-CC-DD-EE-FF", viaArp: true)],
        });

        var link = Assert.Single(graph.Links, l => l.To == "AA-BB-CC-DD-EE-FF");

        Assert.Equal(LinkConfidence.Confirmed, link.Confidence);
        Assert.Contains("ARP", link.Because, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutArpAnswer_MembershipIsOnlyAnAssumption()
    {
        // Ответ на ICMP не доказывает общий домен: пакет мог пройти через
        // маршрутизатор, а попадание адреса в диапазон ничего не гарантирует.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = [Device("192.168.1.50", mac: null)],
        });

        var link = Assert.Single(graph.Links, l => l.To == "192.168.1.50");

        Assert.Equal(LinkConfidence.Assumed, link.Confidence);
    }

    [Fact]
    public void EveryInferredLink_ExplainsItself()
    {
        // Догадка обязана себя объяснять: иначе её нельзя ни проверить, ни оспорить.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet("192.168.1.0/24", "192.168.1.1")],
            Devices = [Device("192.168.1.1", "AA-BB-CC-DD-EE-FF"), Device("192.168.1.50")],
            Paths =
            [
                new PathObservation
                {
                    Destination = "8.8.8.8",
                    Hops = ["10.0.0.1", "8.8.8.8"],
                    ObservedUtc = Observed,
                },
            ],
        });

        Assert.All(
            graph.Links.Where(l => l.Confidence != LinkConfidence.Confirmed),
            link => Assert.False(
                string.IsNullOrWhiteSpace(link.Because),
                $"Связь {link.From} → {link.To} выведена, но не объяснена."));
    }

    // ------------------------------------------------------------ внешние пути

    [Fact]
    public void PathIsAnchoredToTheGateway()
    {
        // Без пристыковки цепочка трассировки повисает в стороне от карты:
        // первый ответивший хоп обычно уже за шлюзом.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet("192.168.1.0/24", "192.168.1.1")],
            Devices = [Device("192.168.1.1", "AA-BB-CC-DD-EE-FF")],
            Paths =
            [
                new PathObservation
                {
                    Destination = "8.8.8.8",
                    Hops = ["10.0.0.1", "8.8.8.8"],
                    ObservedUtc = Observed,
                },
            ],
        });

        var anchor = Assert.Single(graph.Links, l => l.From == "AA-BB-CC-DD-EE-FF" && l.Kind == LinkKind.Path);

        Assert.Equal(LinkConfidence.Inferred, anchor.Confidence);
        Assert.Contains("не ответившие", anchor.Because, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnySubnet_PathHangsOnThisMachine()
    {
        // Так бывает, когда все интерфейсы отфильтрованы или у машины нет адреса IPv4.
        // Путь всё равно измерен отсюда, и повесить его на саму машину честнее,
        // чем оставить цепочку висеть в пустоте.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Paths =
            [
                new PathObservation
                {
                    Destination = "8.8.8.8",
                    Hops = ["10.0.0.1", "8.8.8.8"],
                    ObservedUtc = Observed,
                },
            ],
        });

        var anchor = Assert.Single(
            graph.Links,
            l => l.From == TopologyGraph.ThisMachineId && l.Kind == LinkKind.Path);

        Assert.Equal("10.0.0.1", anchor.To);
        Assert.Equal(LinkConfidence.Inferred, anchor.Confidence);
    }

    [Fact]
    public void HopAdjacency_IsNeverAFact()
    {
        // Соседние хопы трассировки не обязаны быть соседями в сети: туннель MPLS
        // без переноса TTL прячет целые участки пути — это выяснилось в И-7.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Paths =
            [
                new PathObservation
                {
                    Destination = "8.8.8.8",
                    Hops = ["10.0.0.1", "10.0.0.2", "8.8.8.8"],
                    ObservedUtc = Observed,
                },
            ],
        });

        Assert.All(
            graph.Links.Where(l => l.Kind == LinkKind.Path),
            link => Assert.NotEqual(LinkConfidence.Confirmed, link.Confidence));
    }

    [Fact]
    public void SharedPathPrefix_IsNotDuplicated()
    {
        // Две трассировки через общего провайдера делят первые хопы. Карта обязана
        // показать это ветвлением, а не двумя параллельными цепочками.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Paths =
            [
                new PathObservation
                {
                    Destination = "1.1.1.1",
                    Hops = ["10.0.0.1", "10.0.0.2", "1.1.1.1"],
                    ObservedUtc = Observed,
                },
                new PathObservation
                {
                    Destination = "8.8.8.8",
                    Hops = ["10.0.0.1", "10.0.0.2", "8.8.8.8"],
                    ObservedUtc = Observed,
                },
            ],
        });

        Assert.Single(graph.Nodes, n => n.Address == "10.0.0.1");
        Assert.Single(graph.Links, l => l.From == "10.0.0.1" && l.To == "10.0.0.2");

        // Общий узел называет обе трассировки: скрыть, что через него идёт
        // не одно направление, значило бы недосказать.
        var shared = graph.Nodes.Single(n => n.Address == "10.0.0.1");

        Assert.All(TwoDestinations, d => Assert.Contains(d, shared.Detail ?? string.Empty, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ сворачивание

    [Fact]
    public void ManyHosts_AreCollapsedIntoACounter()
    {
        // Порог из спайка-04: триста прямоугольников с адресами — не карта,
        // а список, выложенный в строку.
        var devices = Enumerable.Range(1, 40)
            .Select(i => Device($"192.168.1.{i}", $"AA-BB-CC-DD-EE-{i:X2}"))
            .ToList();

        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = devices,
            CollapseThreshold = 12,
        });

        var group = Assert.Single(graph.Nodes, n => n.Kind == TopologyNodeKind.HostGroup);

        Assert.Equal(28, group.GroupSize);
        Assert.Equal(12, graph.Nodes.Count(n => n.Kind == TopologyNodeKind.Host));
    }

    [Fact]
    public void ExpandedSubnet_ShowsEveryDevice()
    {
        var devices = Enumerable.Range(1, 40)
            .Select(i => Device($"192.168.1.{i}", $"AA-BB-CC-DD-EE-{i:X2}"))
            .ToList();

        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = devices,
            CollapseThreshold = 12,
            ExpandedSubnets = ["192.168.1.0/24"],
        });

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == TopologyNodeKind.HostGroup);
        Assert.Equal(40, graph.Nodes.Count(n => n.Kind == TopologyNodeKind.Host));
    }

    [Fact]
    public void DeviceOutsideSubnet_IsNotAttached()
    {
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices = [Device("10.20.30.40", "AA-BB-CC-DD-EE-FF")],
        });

        Assert.DoesNotContain(graph.Nodes, n => n.Address == "10.20.30.40");
    }

    // ------------------------------------------------------------ детерминизм

    [Fact]
    public void SameEvidence_GivesSameMapRegardlessOfOrder()
    {
        // Главное свойство: пересчёт обязан быть детерминированным, иначе повторное
        // сканирование меняло бы карту произвольно.
        var devices = new List<Device>
        {
            Device("192.168.1.10", "AA-BB-CC-DD-EE-01"),
            Device("192.168.1.20", "AA-BB-CC-DD-EE-02"),
            Device("192.168.1.1", "AA-BB-CC-DD-EE-03"),
        };

        var straight = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet("192.168.1.0/24", "192.168.1.1")],
            Devices = devices,
        });

        var reversed = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet("192.168.1.0/24", "192.168.1.1")],
            Devices = [.. Enumerable.Reverse(devices)],
        });

        Assert.Equal(
            straight.Nodes.Select(n => n.Id),
            reversed.Nodes.Select(n => n.Id));

        Assert.Equal(
            straight.Links.Select(l => (l.From, l.To, l.Kind, l.Confidence)),
            reversed.Links.Select(l => (l.From, l.To, l.Kind, l.Confidence)));
    }

    [Fact]
    public void NodesAreOrderedByAddressNotByIdentity()
    {
        // Тождество это MAC, и сортировка по нему выкладывает соседние адреса
        // вперемешку — карта становится нечитаемой без всякой причины.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet()],
            Devices =
            [
                Device("192.168.1.30", "FF-FF-FF-FF-FF-FF"),
                Device("192.168.1.10", "AA-AA-AA-AA-AA-AA"),
                Device("192.168.1.20", "BB-BB-BB-BB-BB-BB"),
            ],
        });

        var hosts = graph.Nodes.Where(n => n.Kind == TopologyNodeKind.Host).Select(n => n.Address).ToList();

        Assert.Equal(ThreeAddressesInOrder, hosts);
    }

    [Fact]
    public void DuplicateLinks_KeepTheMostConfidentOne()
    {
        // Один узел бывает и шлюзом, и хопом трассировки. Из совпавших связей
        // остаётся самая уверенная — по правилу, а не по порядку добавления.
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet("192.168.1.0/24", "192.168.1.1")],
            Devices = [Device("192.168.1.1", "AA-BB-CC-DD-EE-FF")],
            Paths =
            [
                new PathObservation
                {
                    Destination = "8.8.8.8",
                    Hops = ["192.168.1.1", "8.8.8.8"],
                    ObservedUtc = Observed,
                },
            ],
        });

        var duplicates = graph.Links
            .GroupBy(l => (l.From, l.To, l.Kind))
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void LinksNeverPointAtMissingNodes()
    {
        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [Subnet("192.168.1.0/24", "192.168.1.1")],
            Devices = [Device("192.168.1.1", "AA-BB-CC-DD-EE-FF"), Device("192.168.1.50")],
            Paths =
            [
                new PathObservation
                {
                    Destination = "8.8.8.8",
                    Hops = ["10.0.0.1", "8.8.8.8"],
                    ObservedUtc = Observed,
                },
            ],
        });

        var ids = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(graph.Links, link =>
        {
            Assert.Contains(link.From, ids);
            Assert.Contains(link.To, ids);
        });
    }
}
