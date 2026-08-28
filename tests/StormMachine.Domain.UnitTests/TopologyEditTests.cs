using StormMachine.Domain.Discovery;
using StormMachine.Domain.Topology;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки правок оператора на карте.
/// </summary>
/// <remarks>
/// Главное свойство: правка — это свидетельство, а не пометка на картинке. Карта
/// пересчитывается из свидетельств при каждом сканировании, и правка, записанная
/// в результат, была бы затёрта первым же пересчётом.
/// </remarks>
public sealed class TopologyEditTests
{
    private static readonly DateTimeOffset Observed = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    private static Device Device(string address, string mac)
    {
        Evidence[] evidence =
        [
            Evidence.Of(EvidenceSource.IcmpEcho, EvidenceKind.Alive, "да", Observed),
            Evidence.Of(EvidenceSource.ArpTable, EvidenceKind.MacAddress, mac, Observed),
        ];

        return Domain.Discovery.Device.FromEvidence(address, evidence, Observed, Observed, isOnline: true);
    }

    private static TopologyInput Input(params TopologyEdit[] edits) => new()
    {
        Subnets =
        [
            new LocalSubnet
            {
                Cidr = "192.168.1.0/24",
                InterfaceName = "тестовый",
                InterfaceAddress = "192.168.1.100",
            },
        ],
        Devices =
        [
            Device("192.168.1.10", "AA-AA-AA-AA-AA-AA"),
            Device("192.168.1.20", "BB-BB-BB-BB-BB-BB"),
        ],
        Edits = edits,
    };

    [Fact]
    public void ManualLink_IsConfirmedAndExplained()
    {
        // У человека, который видел провод, свидетельство весомее любой эвристики.
        var graph = TopologyGraph.Build(Input(
            TopologyEdit.Link("AA-AA-AA-AA-AA-AA", "BB-BB-BB-BB-BB-BB", "оператор", "видел провод")));

        var link = Assert.Single(
            graph.Links,
            l => l.From == "AA-AA-AA-AA-AA-AA" && l.To == "BB-BB-BB-BB-BB-BB");

        Assert.Equal(LinkConfidence.Confirmed, link.Confidence);
        Assert.Contains("видел провод", link.Because, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualLink_MayNameNodesByAddress()
    {
        // Оператор набирает то, что видит на экране, а видит он чаще адрес.
        var graph = TopologyGraph.Build(Input(
            TopologyEdit.Link("192.168.1.10", "192.168.1.20", "оператор")));

        Assert.Single(
            graph.Links,
            l => l.From == "AA-AA-AA-AA-AA-AA" && l.To == "BB-BB-BB-BB-BB-BB");
    }

    [Fact]
    public void RemovedLink_DisappearsInBothDirections()
    {
        // Направление, в котором оператор нарисовал отмену, не должно решать:
        // связь либо есть, либо её нет.
        var graph = TopologyGraph.Build(Input(
            TopologyEdit.Unlink("192.168.1.10", "сеть:192.168.1.0/24", "оператор")));

        Assert.DoesNotContain(
            graph.Links,
            l => (l.From == "AA-AA-AA-AA-AA-AA" && l.To.StartsWith("сеть:", StringComparison.Ordinal))
                 || (l.To == "AA-AA-AA-AA-AA-AA" && l.From.StartsWith("сеть:", StringComparison.Ordinal)));
    }

    [Fact]
    public void HiddenNode_TakesItsLinksWithIt()
    {
        // Узел, которого нет, не может быть ни к чему подключён.
        var graph = TopologyGraph.Build(Input(TopologyEdit.Hide("192.168.1.10", "оператор")));

        Assert.DoesNotContain(graph.Nodes, n => n.Id == "AA-AA-AA-AA-AA-AA");
        Assert.DoesNotContain(graph.Links, l => l.From == "AA-AA-AA-AA-AA-AA" || l.To == "AA-AA-AA-AA-AA-AA");
    }

    [Fact]
    public void EditedDevice_IsNeverCollapsed()
    {
        // Если человек нарисовал к узлу связь, он этим узлом занят — спрятать его
        // в счётчик значило бы стереть его же работу.
        var devices = Enumerable.Range(1, 40)
            .Select(i => Device($"192.168.1.{i}", $"AA-BB-CC-DD-EE-{i:X2}"))
            .ToList();

        var graph = TopologyGraph.Build(new TopologyInput
        {
            Subnets = [new LocalSubnet { Cidr = "192.168.1.0/24", InterfaceName = "тестовый" }],
            Devices = devices,
            CollapseThreshold = 5,
            Edits = [TopologyEdit.Link("192.168.1.39", "192.168.1.40", "оператор")],
        });

        // Оба узла из правки видны поимённо, хотя порог свёртки давно превышен.
        Assert.Contains(graph.Nodes, n => n.Address == "192.168.1.39");
        Assert.Contains(graph.Nodes, n => n.Address == "192.168.1.40");
        Assert.Contains(graph.Nodes, n => n.Kind == TopologyNodeKind.HostGroup);
    }

    [Fact]
    public void LinkToMissingNode_IsSkippedButKept()
    {
        // Устройство могло исчезнуть после того, как оператор нарисовал связь.
        // Правка при этом не теряется: вернётся устройство — вернётся и связь.
        var graph = TopologyGraph.Build(Input(
            TopologyEdit.Link("192.168.1.10", "10.20.30.40", "оператор")));

        Assert.DoesNotContain(graph.Links, l => l.To == "10.20.30.40");
        Assert.Contains(graph.Nodes, n => n.Id == "AA-AA-AA-AA-AA-AA");
    }

    [Fact]
    public void EditsSurviveAnyNumberOfRecomputes()
    {
        // Приёмка итерации: правка обязана пережить три пересканирования подряд.
        // Здесь пересканирование — это построение карты заново из тех же свидетельств.
        var edits = new[]
        {
            TopologyEdit.Link("192.168.1.10", "192.168.1.20", "оператор", "видел провод"),
        };

        for (var pass = 0; pass < 3; pass++)
        {
            var graph = TopologyGraph.Build(Input(edits));

            Assert.Single(
                graph.Links,
                l => l.From == "AA-AA-AA-AA-AA-AA"
                     && l.To == "BB-BB-BB-BB-BB-BB"
                     && l.Confidence == LinkConfidence.Confirmed);
        }
    }

    [Fact]
    public void EditOrder_DoesNotChangeTheResult()
    {
        // Правки применяются по времени, а не по порядку в списке: иначе карта
        // зависела бы от того, как хранилище их вернуло.
        TopologyEdit[] edits =
        [
            TopologyEdit.Link("192.168.1.10", "192.168.1.20", "оператор"),
            TopologyEdit.Hide("192.168.1.20", "оператор"),
        ];

        var straight = TopologyGraph.Build(Input(edits));
        var reversed = TopologyGraph.Build(Input([.. Enumerable.Reverse(edits)]));

        Assert.Equal(
            straight.Nodes.Select(n => n.Id),
            reversed.Nodes.Select(n => n.Id));

        Assert.Equal(
            straight.Links.Select(l => (l.From, l.To)),
            reversed.Links.Select(l => (l.From, l.To)));
    }

    [Fact]
    public void NoEdits_LeaveTheMapAsObserved()
    {
        var graph = TopologyGraph.Build(Input());

        Assert.All(graph.Links, l => Assert.DoesNotContain("оператор", l.Because, StringComparison.Ordinal));
    }
}
