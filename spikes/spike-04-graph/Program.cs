// Spike-04 — R-19: выдержит ли готовый движок раскладки граф сети в 300 узлов.
//
// План (docs/03-development-plan.md) ставит этот спайк ДО итерации И-9: при провале
// готовых компонентов пришлось бы писать собственный рендер поверх Skia, а это другой
// объём работ. Проверяется именно раскладка — она доминирует по времени, рисование
// же в Avalonia стоит предсказуемо дёшево.
//
// Граф берётся не случайный, а формы настоящей сети: несколько маршрутизаторов,
// десяток коммутаторов, остальное — конечные узлы. Случайный граф той же плотности
// раскладывается заметно иначе, и мерить на нём — мерить не то.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using Microsoft.Msagl.Layout.MDS;

static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

static void Header(string text)
{
    Console.WriteLine();
    Console.WriteLine($"=== {text} ===");
}

// ------------------------------------------------------------------ модель сети

/// <summary>
/// Строит граф формы настоящей сети: ядро, коммутаторы, конечные узлы
/// и несколько резервных связей между коммутаторами.
/// </summary>
static GeometryGraph BuildNetwork(int hosts, int switches, int routers)
{
    var graph = new GeometryGraph();
    var nodes = new List<Node>();

    Node Add(double width, double height)
    {
        var node = new Node(CurveFactory.CreateRectangle(width, height, new Point()));
        graph.Nodes.Add(node);
        nodes.Add(node);
        return node;
    }

    // Ядро: маршрутизаторы связаны каждый с каждым.
    var core = new List<Node>();
    for (var i = 0; i < routers; i++)
    {
        core.Add(Add(120, 40));
    }

    for (var i = 0; i < core.Count; i++)
    {
        for (var j = i + 1; j < core.Count; j++)
        {
            graph.Edges.Add(new Edge(core[i], core[j]));
        }
    }

    // Коммутаторы: каждый подключён к маршрутизатору, соседние связаны между собой —
    // так выглядит кольцо доступа, которое встречается в любой офисной сети.
    var access = new List<Node>();
    for (var i = 0; i < switches; i++)
    {
        var node = Add(110, 36);
        access.Add(node);
        graph.Edges.Add(new Edge(core[i % core.Count], node));

        if (i > 0 && i % 4 == 0)
        {
            graph.Edges.Add(new Edge(access[i - 1], node));
        }
    }

    // Конечные узлы висят на коммутаторах.
    for (var i = 0; i < hosts; i++)
    {
        var node = Add(140, 30);
        graph.Edges.Add(new Edge(access[i % access.Count], node));
    }

    return graph;
}

// ------------------------------------------------------------------ замер

/// <summary>
/// Свёрнутая форма: только структура сети, а конечные узлы — счётчиком на коммутаторе.
/// </summary>
/// <remarks>
/// Ровно то, что имеет смысл показывать на карте. Триста отдельных прямоугольников
/// с адресами не карта, а список, выложенный в строку.
/// </remarks>
static GeometryGraph BuildCollapsed(int switches, int routers) =>
    BuildNetwork(hosts: 0, switches, routers);

static (double Layout, int Nodes, int Edges, double Width, double Height) Measure(
    string algorithm,
    int hosts,
    int switches,
    int routers)
{
    var graph = BuildNetwork(hosts, switches, routers);

    var settings = algorithm switch
    {
        "sugiyama" => new SugiyamaLayoutSettings
        {
            NodeSeparation = 20,
            LayerSeparation = 40,
            EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.Spline },
        },
        _ => (LayoutAlgorithmSettings)new MdsLayoutSettings
        {
            EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.Spline },
        },
    };

    var watch = Stopwatch.StartNew();
    LayoutHelpers.CalculateLayout(graph, settings, null);
    watch.Stop();

    return (
        watch.Elapsed.TotalMilliseconds,
        graph.Nodes.Count,
        graph.Edges.Count,
        graph.BoundingBox.Width,
        graph.BoundingBox.Height);
}

// ------------------------------------------------------------------ прогон

Console.WriteLine("Spike-04 — раскладка графа сети движком MSAGL (AutomaticGraphLayout 1.1.12),");
Console.WriteLine("тем самым, что AvaloniaGraphControl несёт внутри себя.");

// Прогрев: первый вызов тянет за собой компиляцию и завышает результат —
// та же поправка, что понадобилась измерениям ICMP в И-1.
_ = Measure("sugiyama", 20, 4, 2);
_ = Measure("mds", 20, 4, 2);

int[][] sizes =
[
    [30, 4, 2],
    [80, 8, 3],
    [280, 16, 4],
    [580, 24, 5],
    [980, 40, 6],
];

foreach (var algorithm in new[] { "sugiyama", "mds" })
{
    Header(algorithm == "sugiyama" ? "Ярусная раскладка (Sugiyama)" : "Раскладка по расстояниям (MDS)");
    Console.WriteLine($"  {"узлов",7} {"связей",7} {"лучшее",10} {"худшее",10} "
                      + $"{"полотно",18} {"вытянутость",12}");

    foreach (var size in sizes)
    {
        var runs = new List<double>(3);
        var nodes = 0;
        var edges = 0;
        var width = 0.0;
        var height = 0.0;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (elapsed, n, e, w, h) = Measure(algorithm, size[0], size[1], size[2]);
            runs.Add(elapsed);
            nodes = n;
            edges = e;
            width = w;
            height = h;
        }

        // Вытянутость важнее времени. Полотно шириной в тридцать тысяч точек
        // при высоте в четыреста — это не карта, а список, выложенный в строку:
        // раскладка отработала быстро, а смотреть на неё нельзя.
        var ratio = height > 0 ? width / height : 0;

        Console.WriteLine($"  {nodes,7} {edges,7} {F(runs.Min()) + " мс",10} "
                          + $"{F(runs.Max()) + " мс",10} "
                          + $"{F(width) + " × " + F(height),18} {F(ratio) + " : 1",12}");
    }
}

Header("Свёрнутая форма: только структура, конечные узлы счётчиком");
Console.WriteLine("  Триста отдельных прямоугольников с адресами — не карта, а список.");
Console.WriteLine("  На карте имеет смысл показывать структуру, а хосты сворачивать в счётчик.");
Console.WriteLine();
Console.WriteLine($"  {"узлов",7} {"связей",7} {"время",10} {"полотно",18} {"вытянутость",12}");

foreach (var size in new[] { 16, 40, 120 })
{
    var graph = BuildCollapsed(size, routers: 4);

    var watch = Stopwatch.StartNew();
    LayoutHelpers.CalculateLayout(graph, new SugiyamaLayoutSettings { NodeSeparation = 20, LayerSeparation = 40 }, null);
    watch.Stop();

    var ratio = graph.BoundingBox.Height > 0 ? graph.BoundingBox.Width / graph.BoundingBox.Height : 0;

    Console.WriteLine($"  {graph.Nodes.Count,7} {graph.Edges.Count,7} "
                      + $"{F(watch.Elapsed.TotalMilliseconds) + " мс",10} "
                      + $"{F(graph.BoundingBox.Width) + " × " + F(graph.BoundingBox.Height),18} "
                      + $"{F(ratio) + " : 1",12}");
}

Header("Память");

var before = GC.GetTotalAllocatedBytes(precise: true);
var big = BuildNetwork(280, 16, 4);
LayoutHelpers.CalculateLayout(big, new SugiyamaLayoutSettings(), null);
var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

Console.WriteLine($"  Граф из {big.Nodes.Count} узлов: {allocated / 1024.0 / 1024.0:0.0} МБ выделено за раскладку");
Console.WriteLine($"  Размер полотна: {F(big.BoundingBox.Width)} × {F(big.BoundingBox.Height)} точек");

Header("Вывод");
Console.WriteLine("  Порог отзывчивости: раскладка 300 узлов должна укладываться в 1 секунду —");
Console.WriteLine("  дольше означает, что при каждом пересчёте графа интерфейс будет заметно замирать.");
Console.WriteLine();
Console.WriteLine("  Второй порог, который спайк и обнаружил: вытянутость полотна. Время");
Console.WriteLine("  раскладки может быть отличным, а результат — непригодным для показа.");
