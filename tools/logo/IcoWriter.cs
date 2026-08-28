using SkiaSharp;

namespace StormMachine.Tools.Logo;

/// <summary>
/// Сборка файла .ico из готовых растров.
/// </summary>
/// <remarks>
/// Пишется руками, потому что в базовой библиотеке .NET кодировщика ICO нет,
/// а тащить ради него отдельный пакет в инструмент, который запускают раз в год,
/// дороже, чем сорок строк заголовка.
/// <para>
/// Мелкие размеры кладутся несжатым DIB, крупные — PNG. Windows 10 и 11 понимают
/// PNG в любом размере, но проводник, диалоги и старые оболочки исторически ждали
/// DIB, и у иконки, собранной целиком из PNG, есть привычка не показываться
/// то тут, то там. Смешанный формат — общепринятое решение этого.
/// </para>
/// </remarks>
internal static class IcoWriter
{
    /// <summary>Начиная с этого размера запись хранится PNG.</summary>
    private const int PngFrom = 128;

    public static byte[] Build(Mark mark)
    {
        var entries = new List<(int Size, byte[] Data, bool IsPng)>();

        foreach (var size in IconSizes.All)
        {
            using var bitmap = Render.Tile(mark, size);

            entries.Add(size >= PngFrom
                ? (size, Render.ToPng(bitmap), true)
                : (size, Dib(bitmap), false));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                  // зарезервировано
        writer.Write((ushort)1);                  // тип: иконка
        writer.Write((ushort)entries.Count);

        var offset = 6 + (16 * entries.Count);

        foreach (var (size, data, _) in entries)
        {
            // Ноль в поле размера означает 256: байт больше не вмещает.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);                // палитра не используется
            writer.Write((byte)0);                // зарезервировано
            writer.Write((ushort)1);              // плоскостей
            writer.Write((ushort)32);             // бит на пиксель
            writer.Write(data.Length);
            writer.Write(offset);

            offset += data.Length;
        }

        foreach (var (_, data, _) in entries)
        {
            writer.Write(data);
        }

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// Растр в виде DIB, как его ждёт ICO.
    /// </summary>
    /// <remarks>
    /// Две особенности формата, на которых обычно спотыкаются. Высота в заголовке
    /// удваивается — за цветными пикселями следует маска прозрачности, даже когда
    /// она не нужна. И строки идут снизу вверх.
    /// </remarks>
    private static byte[] Dib(SKBitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var maskStride = ((width + 31) / 32) * 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(40);                          // размер BITMAPINFOHEADER
        writer.Write(width);
        writer.Write(height * 2);                  // цвет + маска
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);                           // без сжатия
        writer.Write(width * height * 4);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                writer.Write(pixel.Blue);
                writer.Write(pixel.Green);
                writer.Write(pixel.Red);
                writer.Write(pixel.Alpha);
            }
        }

        // Маска нулевая: прозрачность несёт альфа-канал. Windows на 32 битах
        // пользуется им, но саму маску формат требует присутствующей.
        writer.Write(new byte[maskStride * height]);

        writer.Flush();

        return stream.ToArray();
    }
}

/// <summary>Тот же знак разметкой SVG — для README и документов.</summary>
internal static class Svg
{
    public static string Write(Mark mark) =>
        $"""
         <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
           <title>Storm Machine — {mark.Title}</title>
           <defs>
             <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
               <stop offset="0" stop-color="#232A38"/>
               <stop offset="1" stop-color="#141922"/>
             </linearGradient>
           </defs>
           <rect x="0" y="0" width="256" height="256" rx="57.6" fill="url(#tile)"/>
           <rect x="1" y="1" width="254" height="254" rx="56.6" fill="none" stroke="#39435A" stroke-width="2"/>
           <g transform="translate(48.64 48.64) scale(158.72)">
             {mark.Svg(false)}
           </g>
         </svg>

         """;
}
