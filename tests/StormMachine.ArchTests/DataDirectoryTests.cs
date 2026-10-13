using System.Text.RegularExpressions;

namespace StormMachine.ArchTests;

/// <summary>
/// Где продукт держит своё, решается в одном месте.
/// </summary>
/// <remarks>
/// До портативного режима каталог данных вычисляли двое: хранилище — для базы,
/// база принадлежности адресов — для своих файлов. Пока оба отвечали «профиль
/// пользователя», расхождения не было видно. Портативность развела ответы сразу:
/// база уехала бы на флешку, а геобаза осталась бы на чужой машине — и подсказка
/// «положите файл сюда» показывала бы каталог, которым продукт не пользуется.
/// <para>
/// Поэтому правило живёт в <c>ProductData</c>, а остальные спрашивают каталог
/// у хранилища через <c>IStorageLocation.DataDirectory</c>.
/// </para>
/// </remarks>
public sealed class DataDirectoryTests
{
    /// <summary>Единственный файл, которому положено знать правило.</summary>
    private const string TheOnePlace = "ProductData.cs";

    [Fact(DisplayName = "Каталог данных: профиль пользователя читает одно место")]
    public void TheUserProfile_IsReadInOnePlaceOnly()
    {
        var violations = new List<string>();

        foreach (var file in Sources())
        {
            if (Path.GetFileName(file) == TheOnePlace)
            {
                continue;
            }

            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, @"SpecialFolder\.(LocalApplicationData|ApplicationData)"))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Каталог данных вычисляется мимо {TheOnePlace}. Второй способ отвечает иначе "
            + "в портативном режиме, и расхождение видно не сразу, а когда человек ищет "
            + "свои измерения и не находит их:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Файлы продукта: плагины и исследовательский код сюда не входят.</summary>
    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(
            Path.Combine(RepositoryLayout.Root, "src"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
