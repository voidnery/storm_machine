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

    /// <summary>
    /// Литеральный цвет допустим только в словаре токенов.
    /// </summary>
    /// <remarks>
    /// Словарь с И-26 — отдельный файл <c>Tokens.axaml</c>, общий для клиента
    /// и окна агента. До этого он жил внутри App.axaml, и окно агента завело
    /// бы себе второй: два словаря разошлись бы на первой правке, и один и тот же
    /// продукт выглядел бы на двух машинах по-разному.
    /// </remarks>
    [Fact]
    public void LiteralColors_LiveOnlyInAppAxaml()
    {
        var violations = new List<string>();

        foreach (var file in MarkupFiles())
        {
            if (Path.GetFileName(file) == "Tokens.axaml")
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

    /// <summary>
    /// Выпадающий список в продукте один — <c>forms:Picker</c>.
    /// </summary>
    /// <remarks>
    /// До И-24+ их было девятнадцать трёх пород: <c>ComboBox</c> со своим шаблоном пункта
    /// на каждой странице, голый <c>ComboBox</c> и поле цели с самодельным списком
    /// из кнопок в <c>Popup</c> — четыре копии по тридцать строк. Оператор увидел то же,
    /// что видно в разметке: «одинаковые по сути списки выглядят по-разному».
    /// Штатные контролы запрещены здесь по той же причине, по какой запрещены
    /// литеральные цвета: следующий экран иначе заведёт двадцатый список.
    /// </remarks>
    [Fact]
    public void Dropdowns_AreTheProductsOwn()
    {
        var violations = new List<string>();

        foreach (var file in MarkupFiles())
        {
            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, "<(ComboBox|AutoCompleteBox)\\b"))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line} — {match.Groups[1].Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Штатный выпадающий список в разметке. Выбор в продукте один — forms:Picker "
            + "(Controls/Picker.cs): у него одна форма пункта, поиск по длинному списку "
            + "и внятная фраза на пустом:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Окно не отправляет оператора в консоль за тем, что умеет само.
    /// </summary>
    /// <remarks>
    /// Замечание оператора: чипы «storm monitors add», «storm alerts set» и им подобные
    /// стояли рядом с формами, которые делают ровно это. Смысл у такой подписи был
    /// один — сказать «а вообще-то есть консоль», и человек, который открыл окно,
    /// читал её как «здесь этого нельзя».
    /// <para>
    /// Правило: если возможность есть в окне — команду не показываем; если её в окне
    /// нет — делаем, а не отсылаем. Исключение одно и оно в этом же тесте: страница
    /// раздела, которого в окне ещё нет, честно называет консольные команды —
    /// иначе она обещала бы, что возможности нет вовсе.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "Разметка: окно не отсылает оператора в консоль")]
    public void Markup_DoesNotSendTheOperatorToTheConsole()
    {
        var violations = new List<string>();

        foreach (var file in MarkupFiles())
        {
            // Заглушка раздела — единственное место, где команда консоли уместна.
            if (Path.GetFileName(file).StartsWith("PlaceholderPage", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, "Text=\"storm [a-zа-я]"))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Команда консоли в разметке окна. Если возможность есть в окне — команда лишняя; "
            + "если её нет — её надо сделать, а не отослать за ней в другую программу:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Ядро не знает синтаксиса консоли.
    /// </summary>
    /// <remarks>
    /// Разметку окна сторож уже проверял — и пропустил ровно тот же отказ, пришедший
    /// с другой стороны: текст «Завести набор: storm snmp creds add "свитчи"
    /// --версия v2c» приехал в окно из <c>CapabilityInspector</c>, то есть из ядра.
    /// Нашёл это оператор, а не проверка.
    /// <para>
    /// Правило: ядро называет <b>действие</b>, а как оно делается — знает клиент.
    /// Консоль печатает свою команду, окно ставит кнопку. Команда в ядре — это
    /// та же команда в обоих клиентах, и в окне она читается как «здесь этого нельзя».
    /// </para>
    /// <para>
    /// Комментарии не в счёт: ссылка на команду в пояснении к коду адресована тому,
    /// кто этот код читает, а не оператору.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "Ядро не отправляет оператора в консоль")]
    public void Core_DoesNotSpeakConsoleSyntax()
    {
        var violations = new List<string>();

        foreach (var file in RepositoryLayout.SourceFiles("src/StormMachine.Application")
                     .Concat(RepositoryLayout.SourceFiles("src/StormMachine.Domain")))
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();

                if (line.StartsWith("///", StringComparison.Ordinal)
                    || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // «storm» и слово через пробел — это команда. Дефис не в счёт:
                // «storm-agent.exe» — имя файла, который ядро и правда собирает,
                // а не предложение сходить в другую программу.
                if (Regex.IsMatch(lines[i], @"storm [a-zа-я]"))
                {
                    violations.Add($"{RepositoryLayout.Relative(file)}:{i + 1} — {line.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Команда консоли в тексте ядра. Ядро называет действие, синтаксис — дело клиента: "
            + "консоль печатает команду, окно ставит кнопку:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Разметка не решает сама, как показать измеренное число.
    /// </summary>
    /// <remarks>
    /// Точность и единица — правило продукта, оно живёт в <c>Units</c>. Формат «0.000»
    /// в привязке давал «244.160 мс»: три знака на двухстах миллисекундах сообщают
    /// точность, которой у сетевого измерения нет.
    /// </remarks>
    [Fact]
    public void Markup_DoesNotFormatMeasuredNumbersItself()
    {
        var violations = new List<string>();

        foreach (var file in MarkupFiles())
        {
            var source = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(source, "StringFormat='[^']*(мс|Мбит)"))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{RepositoryLayout.Relative(file)}:{line}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Единица измерения приписана числу в разметке. Как показать измеренное — "
            + "решает Units (домен), а модель отдаёт готовую строку:"
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
