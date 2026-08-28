using SkiaSharp;

namespace StormMachine.Tools.Logo;

/// <summary>Отрисовка знака в растр.</summary>
internal static class Render
{
    /// <summary>
    /// Отступ от края плитки до знака.
    /// </summary>
    /// <remarks>
    /// В мелких размерах он меньше: при шестнадцати пикселях щедрое поле съедает
    /// половину знака, и от линии остаётся волосок. Правило «одинаковые пропорции
    /// во всех размерах» тут работает против читаемости, а читаемость важнее.
    /// </remarks>
    private static float Padding(bool small) => small ? 0.11f : 0.19f;

    /// <summary>
    /// Плитка со знаком.
    /// </summary>
    /// <remarks>
    /// Подложка тёмная, а не прозрачная: знак живёт в панели задач Windows,
    /// где фон бывает и светлым, и тёмным, и синяя линия на прозрачном фоне
    /// на светлой теме почти пропадает. Плитка даёт знаку собственный фон
    /// и не зависит от чужого.
    /// </remarks>
    public static SKBitmap Tile(Mark mark, int size)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Transparent);

        var small = size < IconSizes.SmallBelow;
        var radius = size * 0.225f;
        var rect = new SKRect(0, 0, size, size);

        using (var background = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, size),
                [Palette.PanelTop, Palette.PanelBottom],
                null,
                SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawRoundRect(rect, radius, radius, background);
        }

        // Кромка нужна на тёмном фоне: без неё плитка сливается с панелью задач
        // в тёмной теме и знак начинает висеть в воздухе.
        if (!small)
        {
            using var edge = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = MathF.Max(1f, size * 0.008f),
                Color = Palette.Edge,
            };

            var inset = edge.StrokeWidth / 2;

            canvas.DrawRoundRect(
                new SKRect(inset, inset, size - inset, size - inset),
                radius - inset,
                radius - inset,
                edge);
        }

        var padding = Padding(small);
        var inner = size * (1 - (2 * padding));

        canvas.Save();
        canvas.Translate(size * padding, size * padding);
        mark.Draw(canvas, inner, small);
        canvas.Restore();

        return bitmap;
    }

    /// <summary>
    /// Лист сравнения: все варианты во всех размерах.
    /// </summary>
    /// <remarks>
    /// Мелкие размеры показываются в натуральную величину, без увеличения:
    /// увеличенная шестнадцатипиксельная иконка выглядит лучше настоящей
    /// и обманывает того, кто выбирает.
    /// </remarks>
    public static SKBitmap Sheet(IReadOnlyList<Mark> marks)
    {
        const int big = 192;
        const int margin = 32;
        const int gap = 28;
        const int rowHeight = big + 96;

        var width = margin + ((big + 220 + gap) * marks.Count) + margin;
        var height = margin + (rowHeight * 1) + margin;

        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColor.Parse("#0F131B"));

        using var title = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var titleFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 22);
        using var note = new SKPaint { Color = SKColor.Parse("#9AA4B8"), IsAntialias = true };
        using var noteFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 13);
        using var tiny = new SKPaint { Color = SKColor.Parse("#6B7488"), IsAntialias = true };
        using var tinyFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 11);

        var x = margin;

        foreach (var mark in marks)
        {
            var y = margin;

            canvas.DrawText(mark.Title, x, y + 20, SKTextAlign.Left, titleFont, title);

            y += 40;

            using (var large = Tile(mark, big))
            {
                canvas.DrawBitmap(large, x, y);
            }

            // Настоящие размеры в ряд под большим — вот где знак и проверяется.
            var sx = x + big + 20;
            var sy = y;

            foreach (var size in IconSizes.All.Where(s => s <= 64))
            {
                using var small = Tile(mark, size);

                canvas.DrawBitmap(small, sx, sy + (64 - size));
                canvas.DrawText(size.ToString(), sx + (size / 2f), sy + 82, SKTextAlign.Center, tinyFont, tiny);

                sx += size + 14;
            }

            y += big + 22;

            foreach (var line in Wrap(mark.About, 34))
            {
                canvas.DrawText(line, x, y, SKTextAlign.Left, noteFont, note);
                y += 18;
            }

            x += big + 220 + gap;
        }

        return bitmap;
    }

    public static byte[] ToPng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = string.Empty;

        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > width)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : $"{line} {word}";
            }
        }

        if (line.Length > 0)
        {
            yield return line;
        }
    }
}
