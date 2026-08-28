using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.MDS;
using Microsoft.Msagl.Miscellaneous;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Topology;
using Node = Microsoft.Msagl.Core.Layout.Node;

namespace StormMachine.Discovery;

/// <summary>
/// Расчёт расположения узлов карты.
/// </summary>
/// <remarks>
/// Берётся только геометрия MSAGL, рисование своё. Причина в том, что достоверность
/// связи обязана быть видна видом линии, а чужие стили под это не гнутся — и потому,
/// что расположение узлов не свойство сети, а свойство показа: в выгрузку JSON
/// координаты не входят вовсе.
/// <para>
/// Раскладка по расстояниям (MDS), а не ярусная. Спайк-04 показал, почему: у сети мало
/// уровней и очень много листьев, и ярусная раскладка даёт полотно с соотношением
/// сторон 65 : 1 — формально успех, на деле список, выложенный в строку. MDS держит
/// около 1.5 : 1 на всех размерах.
/// </para>
/// </remarks>
public sealed class MsaglTopologyLayout : ITopologyLayout
{
    public PlacedGraph Arrange(TopologyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (graph.IsEmpty)
        {
            return PlacedGraph.Empty;
        }

        var geometry = new GeometryGraph();
        var byId = new Dictionary<string, Node>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            var (width, height) = PlacedGraph.SizeOf(node.Kind);
            var shape = new Node(CurveFactory.CreateRectangle(width, height, new Point()));

            geometry.Nodes.Add(shape);
            byId[node.Id] = shape;
        }

        foreach (var link in graph.Links)
        {
            if (byId.TryGetValue(link.From, out var from) && byId.TryGetValue(link.To, out var to))
            {
                geometry.Edges.Add(new Edge(from, to));
            }
        }

        LayoutHelpers.CalculateLayout(
            geometry,
            new MdsLayoutSettings { EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.StraightLine } },
            null);

        // Начало координат переносится в левый верхний угол: MSAGL кладёт полотно
        // вокруг нуля, а рисовать удобнее от угла.
        var offsetX = -geometry.BoundingBox.Left;
        var offsetY = -geometry.BoundingBox.Bottom;

        var placed = new List<PlacedNode>(graph.Nodes.Count);
        var positions = new Dictionary<string, PlacedNode>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            var shape = byId[node.Id];
            var (width, height) = PlacedGraph.SizeOf(node.Kind);

            var item = new PlacedNode(
                node,
                shape.Center.X + offsetX,
                shape.Center.Y + offsetY,
                width,
                height);

            placed.Add(item);
            positions[node.Id] = item;
        }

        var links = new List<PlacedLink>(graph.Links.Count);

        foreach (var link in graph.Links)
        {
            if (positions.TryGetValue(link.From, out var from) && positions.TryGetValue(link.To, out var to))
            {
                links.Add(new PlacedLink(link, from.X, from.Y, to.X, to.Y));
            }
        }

        return new PlacedGraph
        {
            Nodes = placed,
            Links = links,
            Width = geometry.BoundingBox.Width,
            Height = geometry.BoundingBox.Height,
        };
    }
}
