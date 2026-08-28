namespace StormMachine.Domain.Topology;

/// <summary>Где стоит узел и какого он размера.</summary>
public sealed record PlacedNode(TopologyNode Node, double X, double Y, double Width, double Height)
{
    public double Left => X - (Width / 2);

    public double Top => Y - (Height / 2);

    public double Right => X + (Width / 2);

    public double Bottom => Y + (Height / 2);
}

/// <summary>Где проходит связь.</summary>
public sealed record PlacedLink(TopologyLink Link, double X1, double Y1, double X2, double Y2);

/// <summary>
/// Разложенная карта: узлы, связи и размеры полотна.
/// </summary>
/// <remarks>
/// Живёт в домене, хотя расположение узлов — свойство показа, а не сети. Причина
/// в том, что показов стало два: полотно в клиенте и схема в отчёте. Держать
/// две раскладки значило бы получить две разные карты одной сети, а карта, которая
/// в документе выглядит иначе, чем на экране, обесценивает и документ, и экран.
/// <para>
/// В выгрузку JSON координаты по-прежнему не входят: там сеть, а не её изображение.
/// </para>
/// </remarks>
public sealed record PlacedGraph
{
    public required IReadOnlyList<PlacedNode> Nodes { get; init; }

    public required IReadOnlyList<PlacedLink> Links { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public static PlacedGraph Empty { get; } = new() { Nodes = [], Links = [], Width = 0, Height = 0 };

    public bool IsEmpty => Nodes.Count == 0;

    /// <summary>
    /// Размеры прямоугольника узла.
    /// </summary>
    /// <remarks>
    /// Здесь, а не в раскладке: от них зависит и расчёт расположения, и рисование,
    /// и если их развести, узлы начнут наезжать друг на друга ровно в том показе,
    /// который не участвовал в расчёте.
    /// </remarks>
    public static (double Width, double Height) SizeOf(TopologyNodeKind kind) => kind switch
    {
        TopologyNodeKind.ThisMachine => (170, 46),
        TopologyNodeKind.Subnet => (190, 44),
        TopologyNodeKind.Router => (170, 44),
        TopologyNodeKind.Internet => (140, 44),
        TopologyNodeKind.HostGroup => (170, 40),
        _ => (180, 38),
    };
}
