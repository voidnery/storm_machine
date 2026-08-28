using System.Globalization;
using SkiaSharp;
using StormMachine.Domain.Topology;

namespace StormMachine.Reporting;

/// <summary>
/// Схема сети для документа.
/// </summary>
/// <remarks>
/// Раскладка берётся та же, что у полотна на экране: карта, выглядящая в отчёте иначе,
/// чем в клиенте, обесценивает и то и другое.
/// <para>
/// Рисование своё и <b>светлое</b>, а не снимок тёмного экрана. Причина не в красоте:
/// отчёт печатают, и тёмная заливка на бумаге превращается в чёрный прямоугольник,
/// в котором ничего не разобрать.
/// </para>
/// <para>
/// Различие достоверности связи сохранено видом линии — сплошная, штриховая, точечная.
/// Это главное, что карта сообщает, и терять его при переносе в документ нельзя:
/// подтверждённая связь и догадка выглядят по-разному и на экране, и на бумаге.
/// </para>
/// </remarks>
internal static class TopologyDiagramImage
{
    /// <summary>Ширина картинки в точках. Больше ширины страницы A4 за полями — вдвое, для чёткости.</summary>
    private const int Width = 1600;

    private const float Padding = 24;

    private static readonly SKColor Ink = SKColor.Parse("#1F2937");
    private static readonly SKColor Muted = SKColor.Parse("#6B7280");
    private static readonly SKColor Line = SKColor.Parse("#111827");
    private static readonly SKColor Fill = SKColor.Parse("#F3F4F6");
    private static readonly SKColor Edge = SKColor.Parse("#9CA3AF");
    private static readonly SKColor Offline = SKColor.Parse("#B91C1C");

    public static byte[]? TryRender(PlacedGraph placed)
    {
        ArgumentNullException.ThrowIfNull(placed);

        if (placed.IsEmpty || placed.Width <= 0 || placed.Height <= 0)
        {
            return null;
        }

        var scale = (Width - (Padding * 2)) / (float)placed.Width;
        var height = (int)Math.Ceiling((placed.Height * scale) + (Padding * 2));

        using var bitmap = new SKBitmap(Width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.White);
        canvas.Translate(Padding, Padding);
        canvas.Scale(scale);

        DrawLinks(canvas, placed, scale);
        DrawNodes(canvas, placed, scale);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    private static void DrawLinks(SKCanvas canvas, PlacedGraph placed, float scale)
    {
        foreach (var link in placed.Links)
        {
            using var paint = new SKPaint
            {
                Color = Edge,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.6f / scale * 2,
                PathEffect = Dash(link.Link.Confidence, scale),
            };

            canvas.DrawLine(
                (float)link.X1,
                (float)link.Y1,
                (float)link.X2,
                (float)link.Y2,
                paint);
        }
    }

    /// <summary>
    /// Вид линии по достоверности связи.
    /// </summary>
    /// <remarks>
    /// Сплошная — подтверждена измерением. Штриховая — выведена. Точечная — допущение.
    /// Те же три вида, что на экране: оператор, привыкший к карте, читает документ
    /// без переучивания.
    /// </remarks>
    private static SKPathEffect? Dash(LinkConfidence confidence, float scale) => confidence switch
    {
        LinkConfidence.Confirmed => null,
        LinkConfidence.Inferred => SKPathEffect.CreateDash([10 / scale * 2, 6 / scale * 2], 0),
        _ => SKPathEffect.CreateDash([2 / scale * 2, 6 / scale * 2], 0),
    };

    private static void DrawNodes(SKCanvas canvas, PlacedGraph placed, float scale)
    {
        using var fill = new SKPaint { Color = Fill, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var border = new SKPaint
        {
            Color = Line,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f / scale * 2,
        };

        using var label = new SKPaint { Color = Ink, IsAntialias = true };
        using var detail = new SKPaint { Color = Muted, IsAntialias = true };
        using var down = new SKPaint { Color = Offline, IsAntialias = true };

        using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 15);
        using var detailFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);

        foreach (var node in placed.Nodes)
        {
            var rect = new SKRect(
                (float)node.Left,
                (float)node.Top,
                (float)node.Right,
                (float)node.Bottom);

            canvas.DrawRoundRect(rect, 5, 5, fill);
            canvas.DrawRoundRect(rect, 5, 5, border);

            var center = (float)node.X;
            var text = Shorten(node.Node.Label, 24);
            var second = Second(node.Node);

            if (second is null)
            {
                canvas.DrawText(text, center, (float)node.Y + 5, SKTextAlign.Center, labelFont, label);

                continue;
            }

            canvas.DrawText(text, center, (float)node.Y - 2, SKTextAlign.Center, labelFont, label);
            canvas.DrawText(
                Shorten(second, 28),
                center,
                (float)node.Y + 13,
                SKTextAlign.Center,
                detailFont,
                node.Node.IsOnline ? detail : down);
        }
    }

    /// <summary>Вторая строка узла: адрес, размер группы или пометка «не отвечает».</summary>
    private static string? Second(TopologyNode node)
    {
        if (!node.IsOnline)
        {
            return node.Address is { } offline ? $"{offline} — не отвечает" : "не отвечает";
        }

        if (node.GroupSize > 1)
        {
            return $"узлов: {node.GroupSize.ToString(CultureInfo.InvariantCulture)}";
        }

        return node.Address ?? node.Vendor;
    }

    private static string Shorten(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";
}
