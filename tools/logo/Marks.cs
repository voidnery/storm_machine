using SkiaSharp;

namespace StormMachine.Tools.Logo;

/// <summary>Палитра продукта. Знак берёт цвета отсюда, а не заводит свои.</summary>
internal static class Palette
{
    public static readonly SKColor Accent = SKColor.Parse("#3B82F6");
    public static readonly SKColor Danger = SKColor.Parse("#EF4444");
    public static readonly SKColor PanelTop = SKColor.Parse("#232A38");
    public static readonly SKColor PanelBottom = SKColor.Parse("#141922");
    public static readonly SKColor Edge = SKColor.Parse("#39435A");
    public static readonly SKColor Muted = SKColor.Parse("#5A6375");
}

/// <summary>Размеры, в которых знак обязан читаться.</summary>
internal static class IconSizes
{
    /// <summary>
    /// От 16 до 256.
    /// </summary>
    /// <remarks>
    /// Шестнадцать — не «мелкий случай», а основной: столько знак занимает
    /// в панели задач и в проводнике. Всё, что не выживает в шестнадцати
    /// пикселях, из знака выкидывается.
    /// </remarks>
    public static readonly int[] All = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>Ниже этого размера рисуется упрощённая геометрия.</summary>
    public const int SmallBelow = 40;
}

/// <summary>Вариант знака.</summary>
/// <param name="Key">Имя папки и файлов.</param>
/// <param name="Title">Как называется в разговоре.</param>
/// <param name="About">Что он говорит.</param>
/// <param name="Draw">Рисование в единичном квадрате, уже сдвинутом и отмасштабированном.</param>
/// <param name="Svg">Тот же знак разметкой — для документов и README.</param>
internal sealed record Mark(
    string Key,
    string Title,
    string About,
    Action<SKCanvas, float, bool> Draw,
    Func<bool, string> Svg);

/// <summary>
/// Три варианта знака.
/// </summary>
/// <remarks>
/// Выбор между ними — за оператором, поэтому генератор делает все три и кладёт
/// рядом лист сравнения. Показывать один вариант в одном размере и называть это
/// выбором было бы нечестно: знак живёт в шестнадцати пикселях, и там половина
/// удачных идей рассыпается.
/// </remarks>
internal static class Marks
{
    // Ломаная измерения: ровная линия и один всплеск посреди неё.
    // Всплеск один и несимметричный — так выглядит настоящий выброс задержки,
    // а не пила. Симметричный зигзаг читался бы как «сигнал», а не как «беда».
    private static readonly SKPoint[] TracePoints =
    [
        new(0.03f, 0.70f), new(0.15f, 0.70f), new(0.24f, 0.66f), new(0.32f, 0.72f),
        new(0.40f, 0.68f),
        new(0.50f, 0.08f),
        new(0.60f, 0.74f), new(0.67f, 0.52f), new(0.74f, 0.70f),
        new(0.85f, 0.67f), new(0.97f, 0.70f),
    ];

    // То же в шестнадцати пикселях: рябь исчезает, остаются линия и всплеск.
    private static readonly SKPoint[] TraceSmall =
    [
        new(0.03f, 0.72f), new(0.36f, 0.72f),
        new(0.50f, 0.08f),
        new(0.64f, 0.72f), new(0.97f, 0.72f),
    ];

    private const float ThresholdY = 0.32f;

    public static Mark Spike { get; } = new(
        "spike",
        "Всплеск",
        "Ровная линия измерения и один разряд посреди неё. То, ради чего продукт существует.",
        (canvas, size, small) => Trace(canvas, size, small, twoColour: false, threshold: false),
        small => TraceSvg(small, twoColour: false, threshold: false));

    public static Mark Bolt { get; } = new(
        "bolt",
        "Разряд",
        "Классическая молния. Читается мгновенно и не говорит об измерении ничего.",
        (canvas, size, _) =>
        {
            using var paint = new SKPaint { Color = Palette.Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
            using var path = BoltPath();

            canvas.Save();
            canvas.Scale(size);
            canvas.DrawPath(path, paint);
            canvas.Restore();
        },
        _ => $"""
              <path d="{BoltData}" fill="#3B82F6"/>
              """);

    public static Mark Threshold { get; } = new(
        "threshold",
        "Порог",
        "Тот же всплеск, но с порогом и красной вершиной над ним: измерение, порог и вердикт в одном знаке.",
        (canvas, size, small) => Trace(canvas, size, small, twoColour: true, threshold: !small),
        small => TraceSvg(small, twoColour: true, threshold: !small));

    /// <summary>
    /// Тот же всплеск, но целиком тревожного цвета.
    /// </summary>
    /// <remarks>
    /// Значок в трее меняет состояние, и красная вершина для этого не годится:
    /// в шестнадцати пикселях она занимает два пикселя и не видна. Меняется вся
    /// линия — это единственное, что различимо в панели задач боковым зрением,
    /// а именно там значок и замечают.
    /// </remarks>
    public static Mark Alert { get; } = new(
        "alert",
        "Всплеск (тревога)",
        "Состояние значка в трее, когда алерт поднят.",
        (canvas, size, small) => Trace(canvas, size, small, twoColour: false, threshold: false, colour: Palette.Danger),
        small => TraceSvg(small, twoColour: false, threshold: false, colour: "#EF4444"));

    private const string BoltData =
        "M 0.585 0.02 L 0.195 0.565 L 0.415 0.565 L 0.335 0.98 L 0.79 0.415 L 0.555 0.415 Z";

    private static SKPath BoltPath()
    {
        var path = new SKPath();

        path.MoveTo(0.585f, 0.02f);
        path.LineTo(0.195f, 0.565f);
        path.LineTo(0.415f, 0.565f);
        path.LineTo(0.335f, 0.98f);
        path.LineTo(0.79f, 0.415f);
        path.LineTo(0.555f, 0.415f);
        path.Close();

        return path;
    }

    private static void Trace(
        SKCanvas canvas,
        float size,
        bool small,
        bool twoColour,
        bool threshold,
        SKColor? colour = null)
    {
        var points = small ? TraceSmall : TracePoints;

        // Толщина растёт при уменьшении: тонкая линия в шестнадцати пикселях
        // превращается в серую нить, а знак — в пятно.
        var width = (small ? 0.17f : 0.085f) * size;

        using var path = new SKPath();
        path.MoveTo(points[0].X * size, points[0].Y * size);

        for (var i = 1; i < points.Length; i++)
        {
            path.LineTo(points[i].X * size, points[i].Y * size);
        }

        if (threshold)
        {
            using var dash = new SKPaint
            {
                Color = Palette.Muted,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.045f * size,
                StrokeCap = SKStrokeCap.Round,
                PathEffect = SKPathEffect.CreateDash([0.09f * size, 0.075f * size], 0),
            };

            canvas.DrawLine(0.02f * size, ThresholdY * size, 0.98f * size, ThresholdY * size, dash);
        }

        using var line = new SKPaint
        {
            Color = colour ?? Palette.Accent,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        canvas.DrawPath(path, line);

        if (!twoColour)
        {
            return;
        }

        // Часть выше порога перекрашивается отсечением, а не отдельной ломаной:
        // так место перехода цвета всегда совпадает с порогом, а не с точкой,
        // которую пришлось бы вычислять и держать в согласии вручную.
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, size, ThresholdY * size));

        line.Color = Palette.Danger;
        canvas.DrawPath(path, line);

        canvas.Restore();
    }

    private static string TraceSvg(bool small, bool twoColour, bool threshold, string colour = "#3B82F6")
    {
        var points = small ? TraceSmall : TracePoints;
        var width = small ? 0.17f : 0.085f;
        var data = string.Join(
            " ",
            points.Select((p, i) => $"{(i == 0 ? "M" : "L")} {p.X:0.###} {p.Y:0.###}"));

        var parts = new List<string>();

        if (threshold)
        {
            parts.Add(
                $"""<line x1="0.02" y1="{ThresholdY:0.##}" x2="0.98" y2="{ThresholdY:0.##}" stroke="#5A6375" stroke-width="0.045" stroke-linecap="round" stroke-dasharray="0.09 0.075"/>""");
        }

        parts.Add(
            $"""<path d="{data}" fill="none" stroke="{colour}" stroke-width="{width:0.###}" stroke-linecap="round" stroke-linejoin="round"/>""");

        if (twoColour)
        {
            parts.Add(
                $"""
                 <clipPath id="above"><rect x="0" y="0" width="1" height="{ThresholdY:0.##}"/></clipPath>
                 <path d="{data}" fill="none" stroke="#EF4444" stroke-width="{width:0.###}" stroke-linecap="round" stroke-linejoin="round" clip-path="url(#above)"/>
                 """);
        }

        return string.Join("\n  ", parts);
    }
}
