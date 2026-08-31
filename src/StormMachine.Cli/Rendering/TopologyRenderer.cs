using System.Globalization;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Topology;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ карты сети деревом.
/// </summary>
/// <remarks>
/// Достоверность связи обозначается видом линии — это главное, что карта обязана
/// сообщать. Карта, на которой догадка выглядит как факт, хуже отсутствия карты:
/// по ней принимают решения, не зная, что часть нарисованного инструмент домыслил.
/// </remarks>
internal static class TopologyRenderer
{
    /// <summary>Сплошная линия — подтверждено, пунктир — выведено, точки — допущение.</summary>
    private static string Line(LinkConfidence confidence) => confidence switch
    {
        LinkConfidence.Confirmed => "───",
        LinkConfidence.Inferred => "- -",
        _ => "· ·",
    };

    private static string Icon(TopologyNodeKind kind) => kind switch
    {
        TopologyNodeKind.ThisMachine => "◉",
        TopologyNodeKind.Subnet => "▤",
        TopologyNodeKind.Router => "◈",
        TopologyNodeKind.Switch => "▥",
        TopologyNodeKind.HostGroup => "▪▪",
        TopologyNodeKind.ExternalHop => "○",
        TopologyNodeKind.Internet => "☁",
        _ => "▫",
    };

    public static void Write(TopologyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (graph.IsEmpty)
        {
            Console.WriteLine("Карта пуста. Начните со сканирования: storm discover");
            return;
        }

        var byId = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var outgoing = graph.Links
            .GroupBy(l => l.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        Console.WriteLine();
        Console.WriteLine($"Узлов: {graph.Nodes.Count}, связей: {graph.Links.Count} "
                          + $"(подтверждённых {graph.ConfirmedLinks}, выведенных {graph.InferredLinks})");
        Console.WriteLine();

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var drawn = new HashSet<(string, string)>();

        Walk(TopologyGraph.ThisMachineId, string.Empty, isLast: true, byId, outgoing, visited, drawn, isRoot: true);

        WriteOrphans(graph, byId, outgoing, visited, drawn);
        WriteExtraLinks(graph, byId, drawn);
        WriteLegend(graph);
    }

    /// <summary>
    /// Показывает то, до чего дерево не дотянулось.
    /// </summary>
    /// <remarks>
    /// Обход, а не плоский список: несвязанный кусок сети — это тоже кусок сети,
    /// со своей структурой. Выложить его строкой значило бы потерять как раз то,
    /// ради чего карту и смотрят.
    /// </remarks>
    private static void WriteOrphans(
        TopologyGraph graph,
        IReadOnlyDictionary<string, TopologyNode> byId,
        IReadOnlyDictionary<string, List<TopologyLink>> outgoing,
        HashSet<string> visited,
        HashSet<(string, string)> drawn)
    {
        var remaining = graph.Nodes.Where(n => !visited.Contains(n.Id)).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        if (remaining.Count == 0)
        {
            return;
        }

        // Корнями берутся узлы, в которые никто из оставшихся не ведёт: с них
        // начинается цепочка. Если таких нет — граф замкнут в кольцо, и корнем
        // становится первый по порядку.
        var incoming = graph.Links
            .Where(l => remaining.Contains(l.From) && remaining.Contains(l.To))
            .Select(l => l.To)
            .ToHashSet(StringComparer.Ordinal);

        var roots = graph.Nodes
            .Where(n => remaining.Contains(n.Id) && !incoming.Contains(n.Id))
            .Select(n => n.Id)
            .ToList();

        if (roots.Count == 0)
        {
            roots.Add(graph.Nodes.First(n => remaining.Contains(n.Id)).Id);
        }

        Console.WriteLine();
        Console.WriteLine("Вне дерева связей:");

        foreach (var root in roots)
        {
            Walk(root, string.Empty, isLast: true, byId, outgoing, visited, drawn, isRoot: true);
        }
    }

    private static void Walk(
        string id,
        string prefix,
        bool isLast,
        IReadOnlyDictionary<string, TopologyNode> byId,
        IReadOnlyDictionary<string, List<TopologyLink>> outgoing,
        HashSet<string> visited,
        HashSet<(string, string)> drawn,
        bool isRoot = false,
        LinkConfidence confidence = LinkConfidence.Confirmed,
        string? because = null)
    {
        if (!byId.TryGetValue(id, out var node) || !visited.Add(id))
        {
            return;
        }

        if (isRoot)
        {
            Console.WriteLine($"{Icon(node.Kind)} {Describe(node)}");
        }
        else
        {
            var branch = isLast ? "└" : "├";
            Console.WriteLine($"{prefix}{branch}{Line(confidence)} {Icon(node.Kind)} {Describe(node)}");

            // Пояснение печатается только для того, что мы вывели, а не наблюдали:
            // у подтверждённых связей объяснять нечего.
            if (confidence != LinkConfidence.Confirmed && because is not null)
            {
                var pad = prefix + (isLast ? "     " : "│    ");
                Console.WriteLine($"{pad} {because}");
            }
        }

        if (!outgoing.TryGetValue(id, out var children))
        {
            return;
        }

        // Порядок детей — по адресу, а не по тождеству: тождество это MAC, и сортировка
        // по нему выкладывает соседние адреса вперемешку.
        var next = children
            .Where(l => !visited.Contains(l.To))
            .OrderBy(l => byId.TryGetValue(l.To, out var target) ? (int)target.Kind : int.MaxValue)
            .ThenBy(l => byId.TryGetValue(l.To, out var target) ? IpAddressOrder.Of(target.Address) : uint.MaxValue)
            .ThenBy(l => l.To, StringComparer.Ordinal)
            .ToList();
        var childPrefix = isRoot ? string.Empty : prefix + (isLast ? "     " : "│    ");

        for (var i = 0; i < next.Count; i++)
        {
            drawn.Add((next[i].From, next[i].To));

            Walk(
                next[i].To,
                childPrefix,
                i == next.Count - 1,
                byId,
                outgoing,
                visited,
                drawn,
                isRoot: false,
                next[i].Confidence,
                next[i].Because);
        }
    }

    private static string Describe(TopologyNode node)
    {
        var parts = new List<string>(4) { node.Label };

        if (node.Address is { } address && !string.Equals(address, node.Label, StringComparison.Ordinal))
        {
            parts.Add(address);
        }

        // Вендор пропускается, если он и есть подпись: у устройства без имени
        // показывать «Intel Corporate · Intel Corporate» незачем.
        if (node.Vendor is { Length: > 0 } vendor
            && vendor != "—"
            && !string.Equals(vendor, node.Label, StringComparison.Ordinal))
        {
            parts.Add(vendor);
        }

        // Тег категории (И-24). Догадка приходит уже с вопросом — «сервер?» и «сервер»
        // обязаны читаться по-разному.
        if (node.Role is { Length: > 0 } role)
        {
            parts.Add(role);
        }

        if (node.Detail is { Length: > 0 } detail)
        {
            parts.Add(detail);
        }

        var text = string.Join(" · ", parts);

        return node.IsOnline ? text : text + " · не отвечает";
    }

    /// <summary>
    /// Показывает связи, не ставшие рёбрами дерева.
    /// </summary>
    /// <remarks>
    /// Сеть — граф, а не дерево: у узла бывает несколько соседей, и второе ребро
    /// в дерево не помещается. Именно так выглядит связь, нарисованная оператором
    /// между двумя устройствами одной подсети, — и промолчать о ней значило бы
    /// показать, будто правка пропала.
    /// </remarks>
    private static void WriteExtraLinks(
        TopologyGraph graph,
        Dictionary<string, TopologyNode> byId,
        HashSet<(string, string)> drawn)
    {
        var extra = graph.Links
            .Where(l => !drawn.Contains((l.From, l.To)))
            .ToList();

        if (extra.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Связи помимо дерева:");

        foreach (var link in extra)
        {
            var from = byId.TryGetValue(link.From, out var a) ? a.Label : link.From;
            var to = byId.TryGetValue(link.To, out var b) ? b.Label : link.To;

            Console.WriteLine($"  {from} {Line(link.Confidence)} {to}");
            Console.WriteLine($"      {link.Because}");
        }
    }

    private static void WriteLegend(TopologyGraph graph)
    {
        Console.WriteLine();
        Console.WriteLine("  ─── подтверждено  - - выведено по правилу  · · допущение");

        // Отсутствие своих сетей объясняется, а не показывается пустой картой:
        // так бывает, когда все интерфейсы виртуальные и их отфильтровали.
        if (!graph.Nodes.Any(n => n.Kind == TopologyNodeKind.Subnet))
        {
            Console.WriteLine();
            Console.WriteLine("  Ни одной своей сети на карте нет. Обычная причина — все интерфейсы");
            Console.WriteLine("  виртуальные, а они отключены ключом --no-virtual. Уберите его,");
            Console.WriteLine("  чтобы увидеть сети коммутаторов Hyper-V, Docker и VPN.");
        }

        if (graph.InferredLinks > 0)
        {
            var share = graph.Links.Count == 0 ? 0 : graph.InferredLinks * 100.0 / graph.Links.Count;

            Console.WriteLine();
            Console.WriteLine($"  Выведенных связей {graph.InferredLinks} из {graph.Links.Count} "
                              + $"({share.ToString("0", CultureInfo.InvariantCulture)}%). Это не ошибки:");
            // Оговорка меняется по факту: сказать «без SNMP» на карте, построенной
            // с опросом, значило бы объяснять оставшиеся догадки отсутствием того,
            // что уже сделано.
            Console.WriteLine(graph.Nodes.Any(n => n.Kind == TopologyNodeKind.Switch)
                ? "  опрос по SNMP закрыл часть связей, остальные выведены по правилам."
                : "  без SNMP и захвата пакетов часть связей приходится выводить по правилам.");

            Console.WriteLine("  Каждая выведенная связь названа причиной — её можно проверить и оспорить.");
        }

        // Оговорки печатаются последними и отдельно: они говорят не о достоверности
        // связей, а о том, что карту нельзя читать буквально. Связи при этом верны —
        // неверно было бы прочесть их как соседство.
        foreach (var caveat in graph.Caveats)
        {
            Console.WriteLine();
            Console.WriteLine($"  ВНИМАНИЕ: {caveat}");
        }
    }
}
