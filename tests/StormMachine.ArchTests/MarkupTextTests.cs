using System.Text.RegularExpressions;

namespace StormMachine.ArchTests;

/// <summary>
/// Тексты в разметке не должны содержать дыр из пробелов.
/// </summary>
/// <remarks>
/// XML заменяет перевод строки в значении атрибута пробелом, но не сворачивает
/// отступ следующей строки — и многострочный <c>Text="…"</c> вклеивает в показанный
/// оператору текст три десятка пробелов подряд. Найдено оператором на первом же
/// осмотре собранного клиента (И-24): десять текстов в пяти страницах с дырами
/// посреди фраз. Проверка запрещает класс отказа, а не конкретные места: перенос
/// строки внутри значения атрибута в <c>.axaml</c> недопустим вовсе — длинная
/// строка в исходнике лучше дыры на экране.
/// </remarks>
public sealed class MarkupTextTests
{
    /// <summary>Атрибут в .axaml не переносится на следующую строку.</summary>
    [Fact]
    public void AxamlAttributeValues_DoNotSpanLines()
    {
        var violations = new List<string>();

        foreach (var file in MarkupFiles())
        {
            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, "=\\s*\"[^\"]*\""))
            {
                if (!match.Value.Contains('\n', StringComparison.Ordinal))
                {
                    continue;
                }

                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Значение атрибута переносится на следующую строку — отступ вклеится "
            + "в показанный текст пробелами. Текст пишется одной строкой:"
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
