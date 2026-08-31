using System.Text.RegularExpressions;

namespace StormMachine.ArchTests;

/// <summary>
/// Навигация клиента не должна врать — так же, как экран возможностей.
/// </summary>
/// <remarks>
/// Поле «появится в И-N» у раздела повторило судьбу поля <c>Iteration</c>
/// у возможности (урок 9 STATUS.md): «появится в И-13» провисело одиннадцать
/// итераций после И-13, и нашёл это оператор на первом осмотре собранного клиента,
/// а не проверка. <see cref="StormMachine.Application" /> закрыт
/// <c>CapabilityHonestyTests</c>, но карта разделов GUI под тот сторож не попадала.
/// Здесь закрывается класс отказа целиком, в обе стороны: обещание срока в текстах
/// продукта запрещено, а заглушка обязана называть консольные команды —
/// существующие, что и сверяется.
/// </remarks>
public sealed class NavigationHonestyTests
{
    /// <summary>
    /// В текстах продукта нет обещаний «появится в И-N».
    /// </summary>
    /// <remarks>
    /// Обещание срока живёт в docs/03-development-plan.md, где его правит ритуал
    /// закрытия итерации. Зашитое в код, оно не устаревает только пока его кто-то
    /// перечитывает — а это уже дважды не срабатывало (SNMP после И-17, карта после
    /// И-9, разделы после И-13). Запланированная возможность называет итерацию через
    /// поле <c>Iteration</c>, которое сторожит <c>CapabilityHonestyTests</c>.
    /// </remarks>
    [Fact]
    public void ProductSources_DoNotPromiseIterations()
    {
        var violations = new List<string>();

        foreach (var file in RepositoryLayout.SourceFiles("src"))
        {
            var code = RepositoryLayout.StripComments(File.ReadAllText(file));
            if (code.Contains("появится в И", StringComparison.Ordinal)
                || code.Contains("появится в итерации", StringComparison.Ordinal))
            {
                violations.Add(RepositoryLayout.Relative(file));
            }
        }

        Assert.True(
            violations.Count == 0,
            "Текст продукта обещает итерацию — обещание молча устареет, как уже трижды "
            + "случалось. Написать, что возможность есть в консоли, или убрать:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Команды, которые заглушка называет вместо экранной формы, существуют.
    /// </summary>
    /// <remarks>
    /// Иначе честность заглушки держится на внимательности: команду переименовали —
    /// и раздел советует несуществующее. Имена собираются из тех же объявлений,
    /// из которых строится сама консоль: <c>new Command("…")</c> в CLI и паспорта
    /// проб <c>Name = "…"</c>.
    /// </remarks>
    [Fact]
    public void StubSections_NameOnlyExistingCommands()
    {
        var map = File.ReadAllText(Path.Combine(
            RepositoryLayout.Root, "src", "StormMachine.App", "ViewModels", "NavigationMap.cs"));

        var mentioned = Regex.Matches(map, @"storm ([a-z0-9-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Заглушек может не быть вовсе — с И-24 их и нет. Проверка остаётся
        // на случай, когда заглушка появится снова: названное обязано существовать.
        if (mentioned.Count == 0)
        {
            return;
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in RepositoryLayout.SourceFiles("src"))
        {
            var code = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(code, @"new Command\(\s*""([a-z0-9-]+)"""))
            {
                declared.Add(m.Groups[1].Value);
            }

            foreach (Match m in Regex.Matches(code, @"Name = ""([a-z0-9-]+)"""))
            {
                declared.Add(m.Groups[1].Value);
            }
        }

        var missing = mentioned.Where(name => !declared.Contains(name)).ToList();

        Assert.True(
            missing.Count == 0,
            "Заглушка называет команды, которых в продукте нет: "
            + string.Join(", ", missing.Select(n => $"storm {n}")));
    }
}
