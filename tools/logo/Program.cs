using SkiaSharp;
using StormMachine.Tools.Logo;

// Генератор фирменного знака Storm Machine.
//
// Знак рисуется кодом, а не лежит картинкой, по трём причинам.
// Первая: его надо отдавать в семи размерах от 16 до 256, и уменьшение большой
// картинки до 16 пикселей даёт кашу — мелкие размеры рисуются своей геометрией.
// Вторая: цвета берутся из палитры продукта, и когда она поменяется, знак
// пересоберётся, а не разъедется с интерфейсом. Третья: знак, который нечем
// пересоздать, через год становится неприкасаемым.

var output = args.Length > 0 ? args[0] : Path.Combine("..", "..", "assets");

Directory.CreateDirectory(output);

var marks = new Mark[] { Marks.Spike, Marks.Bolt, Marks.Threshold, Marks.Alert };

foreach (var mark in marks)
{
    var directory = Path.Combine(output, mark.Key);
    Directory.CreateDirectory(directory);

    foreach (var size in IconSizes.All)
    {
        using var image = Render.Tile(mark, size);
        File.WriteAllBytes(Path.Combine(directory, $"{mark.Key}-{size}.png"), Render.ToPng(image));
    }

    File.WriteAllBytes(Path.Combine(directory, $"{mark.Key}.ico"), IcoWriter.Build(mark));
    File.WriteAllText(Path.Combine(directory, $"{mark.Key}.svg"), Svg.Write(mark));

    Console.WriteLine($"{mark.Key,-10} {mark.Title}");
}

// Лист сравнения: один файл, на котором все варианты видны во всех размерах сразу.
// Выбирать знак по одной большой картинке нельзя — он живёт в шестнадцати пикселях.
File.WriteAllBytes(Path.Combine(output, "candidates.png"), Render.ToPng(Render.Sheet(marks)));

Console.WriteLine();
Console.WriteLine($"Готово: {Path.GetFullPath(output)}");
