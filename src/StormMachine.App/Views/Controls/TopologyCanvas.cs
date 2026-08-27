using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using StormMachine.Domain.Topology;

namespace StormMachine.App.Views.Controls;

/// <summary>
/// Полотно карты сети.
/// </summary>
/// <remarks>
/// Рисование своё, а не готовым компонентом. Причина одна и главная: <b>достоверность
/// связи обязана быть видна</b>. Карта, на которой догадка выглядит как факт, хуже
/// отсутствия карты — по ней принимают решения, не зная, что часть нарисованного
/// инструмент домыслил. Сплошная линия, пунктир и точки различаются с одного взгляда,
/// и подогнать под это чужие стили дороже, чем нарисовать самому.
/// <para>
/// Раскладку считает MSAGL (спайк-04), рисование идёт одним проходом
/// в <see cref="Render"/>: узлов бывает сотни, и дерево элементов на каждый
/// из них обошлось бы дороже самой отрисовки.
/// </para>
/// </remarks>
public sealed class TopologyCanvas : Control
{
    private const double MinScale = 0.15;
    private const double MaxScale = 3.0;
    private const double Padding = 40;

    public static readonly StyledProperty<TopologyGraph?> GraphProperty =
        AvaloniaProperty.Register<TopologyCanvas, TopologyGraph?>(nameof(Graph));

    public static readonly StyledProperty<TopologyNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<TopologyCanvas, TopologyNode?>(
            nameof(SelectedNode),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    // ------------------------------------------------------------------ оформление

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#151922"));
    private static readonly IBrush NodeFill = new SolidColorBrush(Color.Parse("#1E2635"));
    private static readonly IBrush NodeText = new SolidColorBrush(Color.Parse("#DCE3EF"));
    private static readonly IBrush DimText = new SolidColorBrush(Color.Parse("#8894A8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush Selection = new SolidColorBrush(Color.Parse("#60A5FA"));

    /// <summary>Подтверждённая связь — сплошная и яркая.</summary>
    private static readonly IPen ConfirmedPen = new Pen(new SolidColorBrush(Color.Parse("#4B7FD1")), 1.6);

    /// <summary>Выведенная по правилу — пунктир.</summary>
    private static readonly IPen InferredPen = new Pen(new SolidColorBrush(Color.Parse("#7A8AA5")), 1.3)
    {
        DashStyle = new DashStyle([5, 4], 0),
    };

    /// <summary>Допущение — редкие точки, самая слабая линия из трёх.</summary>
    private static readonly IPen AssumedPen = new Pen(new SolidColorBrush(Color.Parse("#5D6B82")), 1.1)
    {
        DashStyle = new DashStyle([1.5, 4], 0),
    };

    private PlacedGraph _placed = PlacedGraph.Empty;
    private double _scale = 1;
    private Point _origin;
    private Point? _dragFrom;
    private Point _dragOrigin;

    static TopologyCanvas()
    {
        AffectsRender<TopologyCanvas>(GraphProperty, SelectedNodeProperty);
        GraphProperty.Changed.AddClassHandler<TopologyCanvas>((canvas, _) => canvas.Rebuild());
    }

    public TopologyGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public TopologyNode? SelectedNode
    {
        get => GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    /// <summary>Разложенная карта — нужна для выгрузки в SVG.</summary>
    public PlacedGraph Placed => _placed;

    private void Rebuild()
    {
        _placed = Graph is { } graph ? TopologyLayout.Arrange(graph) : PlacedGraph.Empty;
        FitToView();
        InvalidateVisual();
    }

    /// <summary>Вписывает карту в окно целиком.</summary>
    public void FitToView()
    {
        if (_placed.Width <= 0 || _placed.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _scale = 1;
            _origin = new Point(Padding, Padding);
            return;
        }

        var scaleX = (Bounds.Width - (Padding * 2)) / _placed.Width;
        var scaleY = (Bounds.Height - (Padding * 2)) / _placed.Height;

        _scale = Math.Clamp(Math.Min(scaleX, scaleY), MinScale, MaxScale);

        _origin = new Point(
            ((Bounds.Width - (_placed.Width * _scale)) / 2) - 0,
            ((Bounds.Height - (_placed.Height * _scale)) / 2) - 0);

        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        FitToView();
    }

    // ------------------------------------------------------------------ управление

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var position = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        var scale = Math.Clamp(_scale * factor, MinScale, MaxScale);

        // Точка под курсором остаётся на месте: иначе при каждом повороте колеса
        // карта уезжает, и найденный узел приходится искать заново.
        var before = ToGraph(position);
        _scale = scale;
        var after = ToGraph(position);

        _origin += new Point((after.X - before.X) * _scale, (after.Y - before.Y) * _scale);

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var position = e.GetPosition(this);
        var hit = HitTest(position);

        if (hit is not null)
        {
            SelectedNode = hit.Node;
        }

        _dragFrom = position;
        _dragOrigin = _origin;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragFrom is not { } from)
        {
            return;
        }

        var position = e.GetPosition(this);
        _origin = _dragOrigin + (position - from);

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    private Point ToGraph(Point screen) =>
        new((screen.X - _origin.X) / _scale, (screen.Y - _origin.Y) / _scale);

    private PlacedNode? HitTest(Point screen)
    {
        var point = ToGraph(screen);

        foreach (var node in _placed.Nodes)
        {
            if (point.X >= node.Left && point.X <= node.Right
                && point.Y >= node.Top && point.Y <= node.Bottom)
            {
                return node;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ рисование

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(Background, new Rect(Bounds.Size));

        if (_placed.Nodes.Count == 0)
        {
            DrawEmpty(context);
            return;
        }

        // Связи рисуются первыми, чтобы прямоугольники узлов их перекрывали:
        // линия, проходящая поверх подписи, делает подпись нечитаемой.
        foreach (var link in _placed.Links)
        {
            context.DrawLine(
                PenFor(link.Link.Confidence),
                ToScreen(link.X1, link.Y1),
                ToScreen(link.X2, link.Y2));
        }

        foreach (var node in _placed.Nodes)
        {
            DrawNode(context, node);
        }
    }

    private static IPen PenFor(LinkConfidence confidence) => confidence switch
    {
        LinkConfidence.Confirmed => ConfirmedPen,
        LinkConfidence.Inferred => InferredPen,
        _ => AssumedPen,
    };

    private Point ToScreen(double x, double y) => new(_origin.X + (x * _scale), _origin.Y + (y * _scale));

    private void DrawNode(DrawingContext context, PlacedNode node)
    {
        var topLeft = ToScreen(node.Left, node.Top);
        var size = new Size(node.Width * _scale, node.Height * _scale);
        var rect = new Rect(topLeft, size);

        var selected = SelectedNode is { } selection && selection.Id == node.Node.Id;
        var border = selected
            ? new Pen(Selection, 2)
            : new Pen(BorderFor(node.Node.Kind), node.Node.Kind == TopologyNodeKind.ThisMachine ? 1.8 : 1);

        context.DrawRectangle(NodeFill, border, rect, 4, 4);

        // Подписи ниже определённого масштаба не рисуются вовсе: они превращаются
        // в нечитаемую кашу и стоят дороже всего остального рисования вместе взятого.
        if (_scale < 0.35)
        {
            return;
        }

        var label = new FormattedText(
            Shorten(node.Node.Label, 24),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12 * _scale,
            node.Node.IsOnline ? NodeText : DimText);

        context.DrawText(label, new Point(rect.X + (8 * _scale), rect.Y + (5 * _scale)));

        var second = Secondary(node.Node);

        if (second is null || _scale < 0.6)
        {
            return;
        }

        var detail = new FormattedText(
            Shorten(second, 28),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            9.5 * _scale,
            DimText);

        context.DrawText(detail, new Point(rect.X + (8 * _scale), rect.Y + (21 * _scale)));
    }

    private static IBrush BorderFor(TopologyNodeKind kind) => kind switch
    {
        TopologyNodeKind.ThisMachine => Accent,
        TopologyNodeKind.Router => Accent,
        TopologyNodeKind.Subnet => new SolidColorBrush(Color.Parse("#4B5A72")),
        TopologyNodeKind.Internet => Warning,
        TopologyNodeKind.HostGroup => new SolidColorBrush(Color.Parse("#4B5A72")),
        _ => new SolidColorBrush(Color.Parse("#333D4E")),
    };

    private static string? Secondary(TopologyNode node)
    {
        if (node.Address is { } address && !string.Equals(address, node.Label, StringComparison.Ordinal))
        {
            return node.Vendor is { Length: > 0 } vendor && vendor != "—" && vendor != node.Label
                ? $"{address} · {vendor}"
                : address;
        }

        return node.Vendor is { Length: > 0 } v && v != "—" && v != node.Label ? v : node.Detail;
    }

    private void DrawEmpty(DrawingContext context)
    {
        var text = new FormattedText(
            "Карта пуста. Начните со сканирования в разделе «Обнаружение».",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            13,
            DimText);

        context.DrawText(text, new Point(
            (Bounds.Width - text.Width) / 2,
            (Bounds.Height - text.Height) / 2));
    }

    private static string Shorten(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";
}
