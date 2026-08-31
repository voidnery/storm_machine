using System.Text.RegularExpressions;

namespace StormMachine.ArchTests;

/// <summary>
/// Разметка страниц пользуется токенами дизайн-системы, а не литералами.
/// </summary>
/// <remarks>
/// До волны 1 (И-24+) в страницах жили 83 литеральных цвета на 25 оттенков при
/// шести токенах в словаре, четырнадцать ступеней шрифта и десять ступеней
/// «серости» через <c>Opacity</c>. Каждый новый экран добавлял свои оттенки, и
/// палитра расползалась незаметно — по одному правдоподобному литералу за раз.
/// Проверки запрещают класс отказа: цвет и ступень серости берутся только из
/// словаря <c>App.axaml</c>, размер шрифта — только со шкалы. Новый оттенок
/// сначала становится токеном со смыслом, потом попадает в разметку.
/// </remarks>
public sealed class DesignTokenTests
{
    /// <summary>Шкала шрифтов продукта. Новая ступень — осознанное решение, не опечатка.</summary>
    private static readonly string[] FontScale = ["10", "11", "12", "13", "18", "24"];

    /// <summary>Литеральный цвет допустим только в словаре токенов App.axaml.</summary>
    [Fact]
    public void LiteralColors_LiveOnlyInAppAxaml()
    {
        var violations = new List<string>();

        foreach (var file in MarkupFiles())
        {
            if (Path.GetFileName(file) == "App.axaml")
            {
                continue;
            }

            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, "#[0-9A-Fa-f]{3,8}\\b"))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line} — {match.Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Литеральный цвет в разметке страницы. Цвета берутся из словаря токенов "
            + "App.axaml через StaticResource; нет подходящего — сначала завести токен:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Размер шрифта — только со шкалы 10/11/12/13/18/24.</summary>
    [Fact]
    public void FontSizes_StayOnScale()
    {
        var violations = new List<string>();

        foreach (var file in MarkupFiles())
        {
            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, "FontSize=\"([0-9.]+)\""))
            {
                if (FontScale.Contains(match.Groups[1].Value))
                {
                    continue;
                }

                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line} — {match.Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Размер шрифта вне шкалы ({string.Join("/", FontScale)}). Промежуточная "
            + "ступень дробит иерархию и не читается как уровень:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Серость текста — токенами TextSecondary/TextMuted, не прозрачностью.</summary>
    /// <remarks>
    /// <c>Opacity</c> на тексте даёт непредсказуемый итоговый цвет (зависит от фона
    /// под элементом) и плодит ступени: до волны 1 их было десять. Для фигур и
    /// привязанного к данным затухания строк прозрачность остаётся законной.
    /// </remarks>
    [Fact]
    public void TextBlocks_UseGrayTokensNotOpacity()
    {
        var violations = new List<string>();

        foreach (var file in MarkupFiles())
        {
            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, "<TextBlock[^>]*\\sOpacity=\"[0-9.]+\""))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Числовая Opacity на TextBlock. Приглушённый текст — это Foreground "
            + "с токеном TextSecondaryBrush или TextMutedBrush:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> MarkupFiles()
    {
        var path = Path.Combine(RepositoryLayout.Root, "src");
        return Directory.EnumerateFiles(path, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
