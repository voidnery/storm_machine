using System.Text.RegularExpressions;

namespace StormMachine.ArchTests;

/// <summary>
/// Слова продукта живут в одном месте на все клиенты.
/// </summary>
/// <remarks>
/// У продукта два лица — консоль и окно, — и оператор пользуется обоими, сверяя одно
/// с другим. Расхождение слов он читает как расхождение состояний: «работает»
/// в окне и «доступно» в консоли выглядят как два разных ответа на один вопрос.
/// <para>
/// Так уже случалось трижды: вердикт (<c>VerdictWording</c>), единицы измерения
/// (<c>Units</c>) и — к И-24+ — тип адаптера (семь копий, четыре печатали
/// «не определён», три «тип не определён») и состояние возможности (пять слов
/// из восьми разошлись). Каждый раз копия появлялась не по ошибке, а потому что
/// написать <c>switch</c> на месте быстрее, чем найти словарь. Эти проверки
/// делают быстрый путь заметным.
/// </para>
/// </remarks>
public sealed class WordingTests
{
    [Fact]
    public void AdapterKind_IsNamedInOnePlace() => Single(
        "AdapterKind\\.Physical\\s*=>\\s*\"",
        "AdapterWording.cs",
        "Тип адаптера назван словами мимо словаря. Слово идёт и в PDF заказчику, "
        + "и в строку состояния клиента, и в вывод storm env: "
        + "AdapterWording.Kind — единственное место, где оно выбирается.");

    [Fact]
    public void CapabilityState_IsNamedInOnePlace() => Single(
        "CapabilityState\\.Available\\s*=>\\s*\"",
        "CapabilityWording.cs",
        "Состояние возможности названо словами мимо словаря. "
        + "CapabilityWording.State — единственное место, где оно выбирается.");

    [Fact]
    public void VerdictLevel_IsMarkedInOnePlace() => Single(
        "VerdictLevel\\.Pass\\s*=>\\s*\"[✓+]",
        "VerdictWording.cs",
        "Знак вердикта написан мимо словаря. VerdictWording.Mark — единственное место.");

    /// <summary>
    /// Доля наблюдавшегося окна сравнивается с порогом только в домене.
    /// </summary>
    /// <remarks>
    /// Порог доверия к покрытию был написан трижды и разошёлся: консоль ставила
    /// пометку у числа с 0.95, а оговорку под ним — с 0.9. При покрытии 0.92 продукт
    /// говорил рядом «часть окна не наблюдалась» и тут же молчал в оговорке.
    /// </remarks>
    [Fact]
    public void CoverageThreshold_LivesInTheDomain()
    {
        var violations = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (Path.GetFileName(file) == "Availability.cs")
            {
                continue;
            }

            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, @"Coverage\s*[<>]=?\s*0\.\d+"))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line} — {match.Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Порог покрытия сравнивается на месте. Достаточно ли наблюдалось окно — "
            + "решает домен: Availability.TrustedCoverage и Availability.CoverageNotice:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static void Single(string pattern, string home, string explanation)
    {
        var violations = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (Path.GetFileName(file) == home)
            {
                continue;
            }

            var source = File.ReadAllText(file);

            if (Regex.IsMatch(source, pattern))
            {
                violations.Add(RepositoryLayout.Relative(file));
            }
        }

        Assert.True(
            violations.Count == 0,
            explanation + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> SourceFiles()
    {
        var path = Path.Combine(RepositoryLayout.Root, "src");

        return Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
