using System.Globalization;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Topology;

namespace StormMachine.LoadTests;

/// <summary>
/// Карта сети на 4096 узлах.
/// </summary>
/// <remarks>
/// Число из плана И-19. Оно не круглое и не случайное: 4096 — это /20, то есть сеть
/// среднего предприятия целиком. Продукт до сих пор видел десятки узлов на стенде,
/// и утверждать что-либо о его поведении на настоящем объекте было не на чем.
/// <para>
/// Проверяется не только время. Пересчёт карты — детерминированная функция от свидетельств
/// (принцип 5), и на четырёх тысячах узлов это утверждение стоит проверить: именно там
/// недетерминированность, если она есть, перестаёт быть незаметной.
/// </para>
/// </remarks>
[Trait("Категория", "Нагрузка")]
public sealed class TopologyLoadTests : IDisposable
{
    private const int NodeCount = 4096;

    public void Dispose() => Measured.Save("load-topology.txt");

    /// <summary>
    /// Карта на 4096 узлов строится за разумное время.
    /// </summary>
    /// <remarks>
    /// Порог в пять секунд взят как граница ожидания: дольше — и оператор решит,
    /// что продукт завис. Настоящая ценность здесь не в пороге, а в числе, попавшем
    /// в протокол: с ним можно сравнить следующий замер.
    /// </remarks>
    [Fact]
    public void Map_OfFourThousandNodes_BuildsWithinReason()
    {
        Measured.Note($"Карта сети, {NodeCount} узлов");

        var input = BuildInput(NodeCount);

        var (graph, elapsed, allocated) = Measured.Run("построение графа", () => TopologyGraph.Build(input));

        Measured.Note($"  узлов на карте: {graph.Nodes.Count}, связей: {graph.Links.Count}");
        Measured.Note(string.Empty);

        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"Карта на {NodeCount} узлов строилась {elapsed.TotalSeconds:N1} с — оператор решит, что продукт завис.");

        // Расход памяти назван, а не проверен порогом: разумного порога тут нет,
        // но десятикратный рост в следующем замере будет виден сразу.
        Assert.True(allocated > 0);
    }

    /// <summary>
    /// Свёртка листьев работает: карта не превращается в список из четырёх тысяч прямоугольников.
    /// </summary>
    /// <remarks>
    /// Порог свёртки взят из спайка-04: триста отдельных прямоугольников с адресами —
    /// не карта, а список, выложенный в строку. На четырёх тысячах узлов вопрос
    /// перестаёт быть вкусовым.
    /// </remarks>
    [Fact]
    public void Map_CollapsesLeavesInsteadOfDrawingThemAll()
    {
        var graph = TopologyGraph.Build(BuildInput(NodeCount));

        var hosts = graph.Nodes.Count(n => n.Kind == TopologyNodeKind.Host);
        var groups = graph.Nodes.Count(n => n.Kind == TopologyNodeKind.HostGroup);

        Measured.Note($"Свёртка: показано поимённо {hosts}, свёрнуто в {groups} групп");
        Measured.Note(string.Empty);

        Assert.True(groups > 0, "Ни одна группа не свёрнута — карта на 4096 узлов нечитаема.");
        Assert.True(
            hosts < NodeCount / 4,
            $"Поимённо показано {hosts} узлов из {NodeCount} — это уже не карта, а список.");
    }

    /// <summary>
    /// Одни и те же свидетельства дают одну и ту же карту.
    /// </summary>
    /// <remarks>
    /// Принцип 5: пересчёт — детерминированная функция от свидетельств. На стенде это
    /// проверялось на десятке узлов, где совпадение могло быть случайным следствием
    /// порядка вставки. На четырёх тысячах словарь и множество уже успевают
    /// перераспределиться, и утверждение становится содержательным.
    /// </remarks>
    [Fact]
    public void Map_IsTheSameFunctionOfTheSameEvidence()
    {
        var first = TopologyGraph.Build(BuildInput(NodeCount));
        var second = TopologyGraph.Build(BuildInput(NodeCount));

        Assert.Equal(first.Nodes.Count, second.Nodes.Count);
        Assert.Equal(first.Links.Count, second.Links.Count);

        Assert.Equal(
            first.Nodes.Select(n => n.Id).ToList(),
            second.Nodes.Select(n => n.Id).ToList());

        Assert.Equal(
            first.Links.Select(l => $"{l.From}->{l.To}:{l.Confidence}").ToList(),
            second.Links.Select(l => $"{l.From}->{l.To}:{l.Confidence}").ToList());
    }

    /// <summary>
    /// Время растёт не быстрее, чем число узлов.
    /// </summary>
    /// <remarks>
    /// Это и есть настоящий предмет нагрузочного прогона: не «уложились ли в пять
    /// секунд сегодня», а «во что это превратится на вдвое большей сети». Квадратичная
    /// зависимость на четырёх тысячах ещё терпима, а на восьми — уже нет, и увидеть её
    /// надо здесь, а не у заказчика.
    /// </remarks>
    [Fact]
    public void Map_ScalesRoughlyLinearly()
    {
        Measured.Note("Рост времени построения от числа узлов");

        var timings = new List<(int Nodes, double Ms)>();

        foreach (var count in new[] { 512, 1024, 2048, 4096 })
        {
            var input = BuildInput(count);

            // Прогрев: первый вызов платит за раскрутку JIT, и без него
            // самый маленький размер выглядел бы самым медленным.
            TopologyGraph.Build(input);

            var (_, elapsed, _) = Measured.Run(
                $"{count.ToString(CultureInfo.InvariantCulture)} узлов",
                () => TopologyGraph.Build(input));

            timings.Add((count, elapsed.TotalMilliseconds));
        }

        var small = timings[0];
        var large = timings[^1];

        var nodeGrowth = (double)large.Nodes / small.Nodes;
        var timeGrowth = large.Ms / Math.Max(small.Ms, 0.001);

        Measured.Note(
            $"  узлов больше в {nodeGrowth:N0} раз, времени больше в {timeGrowth:N1} раз");
        Measured.Note(string.Empty);

        // Квадратичный рост дал бы 64 при восьмикратном росте числа узлов.
        // Запас до 24 оставлен под шум измерения на занятой машине.
        Assert.True(
            timeGrowth < 24,
            $"Время выросло в {timeGrowth:N1} раз при росте числа узлов в {nodeGrowth:N0} — "
            + "похоже на квадратичную зависимость. На вдвое большей сети это станет непригодным.");
    }

    /// <summary>
    /// Собирает сеть из <paramref name="count"/> узлов в шестнадцати подсетях.
    /// </summary>
    /// <remarks>
    /// Шестнадцать подсетей по /24 — это /20 целиком, обычная нарезка предприятия.
    /// Данные строятся детерминированно, без случайных чисел: нагрузочный прогон,
    /// который на каждом запуске меряет другую сеть, не с чем сравнивать.
    /// </remarks>
    private static TopologyInput BuildInput(int count)
    {
        var moment = DateTimeOffset.UnixEpoch;
        var subnets = new List<LocalSubnet>();
        var devices = new List<Device>(count);
        var perSubnet = Math.Max(1, count / 16);

        for (var s = 0; s < 16; s++)
        {
            var third = s.ToString(CultureInfo.InvariantCulture);

            subnets.Add(new LocalSubnet
            {
                Cidr = $"10.0.{third}.0/24",
                InterfaceName = "Ethernet",
                InterfaceAddress = $"10.0.{third}.2",
                Gateways = [$"10.0.{third}.1"],
            });

            for (var host = 0; host < perSubnet && devices.Count < count; host++)
            {
                // Адреса начинаются с .10: единица занята шлюзом, двойка — нами.
                var last = 10 + (host % 240);
                var address = $"10.0.{third}.{last.ToString(CultureInfo.InvariantCulture)}";

                // MAC уникален и выводится из номера: тождество устройства держится
                // на нём, и повтор превратил бы тысячи узлов в десяток.
                var mac = string.Create(
                    CultureInfo.InvariantCulture,
                    $"02:00:{s:X2}:{host / 256:X2}:{host % 256:X2}:{devices.Count % 256:X2}");

                devices.Add(new Device
                {
                    Address = address,
                    Addresses = [address],
                    MacAddress = mac,
                    HostName = $"host-{devices.Count.ToString(CultureInfo.InvariantCulture)}",
                    FirstSeenUtc = moment,
                    LastSeenUtc = moment,
                    IsOnline = true,
                });
            }
        }

        return new TopologyInput
        {
            Devices = devices,
            Subnets = subnets,
        };
    }
}
