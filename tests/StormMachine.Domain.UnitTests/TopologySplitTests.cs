using StormMachine.Domain.Discovery;
using StormMachine.Domain.Topology;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Разбиение карты на листы.
/// </summary>
/// <remarks>
/// Долг И-15: схема сети в отчёте не масштабировалась под большие сети. Полсотни узлов
/// ложатся в страницу A4 читаемо, две сотни — уже нет: схема вписывается в ширину
/// страницы целиком, и подписи становятся мельче того, что глаз разбирает.
/// <para>
/// Разбиение <b>по подсетям</b>, а не механической нарезкой на плитки: оператор думает
/// о своей сети подсетями, и лист, на котором половина одной и четверть другой,
/// читается хуже целого.
/// </para>
/// </remarks>
public sealed class TopologySplitTests
{
    private static readonly DateTimeOffset Moment = DateTimeOffset.UnixEpoch;

    private static TopologyGraph Build(int subnets, int hostsEach)
    {
        var devices = new List<Device>();
        var nets = new List<LocalSubnet>();

        for (var s = 0; s < subnets; s++)
        {
            nets.Add(new LocalSubnet
            {
                Cidr = $"10.0.{s}.0/24",
                InterfaceName = "Ethernet",
                InterfaceAddress = $"10.0.{s}.2",
                Gateways = [$"10.0.{s}.1"],
            });

            for (var h = 0; h < hostsEach; h++)
            {
                var address = $"10.0.{s}.{10 + h}";

                devices.Add(new Device
                {
                    Address = address,
                    Addresses = [address],
                    MacAddress = $"02:00:{s:X2}:00:00:{h:X2}",
                    FirstSeenUtc = Moment,
                    LastSeenUtc = Moment,
                    IsOnline = true,
                });
            }
        }

        return TopologyGraph.Build(new TopologyInput
        {
            Devices = devices,
            Subnets = nets,

            // Сворачивание выключено: разбиение проверяется на настоящем числе узлов,
            // а не на числе после свёртки.
            CollapseThreshold = int.MaxValue,
        });
    }

    /// <summary>Маленькая карта не делится: дробить читаемое незачем.</summary>
    [Fact]
    public void SmallMap_IsNotSplit()
    {
        var graph = Build(subnets: 2, hostsEach: 5);

        Assert.False(graph.IsTooLargeForOnePage);
        Assert.Empty(graph.SplitBySubnet());
    }

    /// <summary>Большая карта делится по листу на подсеть.</summary>
    [Fact]
    public void LargeMap_IsSplitPerSubnet()
    {
        var graph = Build(subnets: 4, hostsEach: 40);

        Assert.True(graph.IsTooLargeForOnePage);

        var sheets = graph.SplitBySubnet();

        Assert.Equal(4, sheets.Count);

        // Каждый лист заметно меньше целого — иначе разбиение ничего не дало бы.
        Assert.All(sheets, sheet => Assert.True(sheet.Graph.Nodes.Count < graph.Nodes.Count));
    }

    /// <summary>
    /// Одна большая подсеть не делится: делить её было бы нечем.
    /// </summary>
    /// <remarks>
    /// Разбиение по подсетям упирается в то, что подсеть должна быть не одна.
    /// Нарезать единственную на плитки — как раз тот вариант, от которого отказались:
    /// лист с четвертью подсети не отвечает ни на один вопрос целиком.
    /// </remarks>
    [Fact]
    public void SingleLargeSubnet_IsNotSplit()
    {
        var graph = Build(subnets: 1, hostsEach: 200);

        Assert.True(graph.IsTooLargeForOnePage);
        Assert.Empty(graph.SplitBySubnet());
    }

    /// <summary>
    /// Своя машина и выход наружу есть на каждом листе.
    /// </summary>
    /// <remarks>
    /// Без них лист теряет то, ради чего карта и рисуется: куда эта подсеть выходит.
    /// Повторение на каждом листе — не дублирование, а условие читаемости.
    /// </remarks>
    [Fact]
    public void EverySheet_KeepsTheWayOut()
    {
        var sheets = Build(subnets: 3, hostsEach: 40).SplitBySubnet();

        Assert.NotEmpty(sheets);

        Assert.All(sheets, sheet =>
            Assert.Contains(sheet.Graph.Nodes, n => n.Kind == TopologyNodeKind.ThisMachine));
    }

    /// <summary>Лист называется своей подсетью — иначе их не различить.</summary>
    [Fact]
    public void EverySheet_IsNamed()
    {
        var sheets = Build(subnets: 3, hostsEach: 40).SplitBySubnet();

        Assert.All(sheets, sheet => Assert.False(string.IsNullOrWhiteSpace(sheet.Title)));
        Assert.Equal(sheets.Count, sheets.Select(s => s.Title).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// На листе нет связей в никуда.
    /// </summary>
    /// <remarks>
    /// Связь, у которой один конец не попал на лист, нарисовалась бы линией
    /// в пустоту — и читалась бы как обрыв там, где его нет.
    /// </remarks>
    [Fact]
    public void SheetLinks_StayInsideTheSheet()
    {
        foreach (var (_, sheet) in Build(subnets: 3, hostsEach: 40).SplitBySubnet())
        {
            var ids = sheet.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

            Assert.All(sheet.Links, link =>
            {
                Assert.Contains(link.From, ids);
                Assert.Contains(link.To, ids);
            });
        }
    }

    /// <summary>Оговорки едут на каждый лист: они про сеть, а не про лист.</summary>
    [Fact]
    public void Caveats_TravelToEverySheet()
    {
        var graph = Build(subnets: 3, hostsEach: 40) with { Caveats = ["проверочная оговорка"] };

        Assert.All(graph.SplitBySubnet(), sheet => Assert.Single(sheet.Graph.Caveats));
    }
}
