using System.Text.RegularExpressions;

namespace StormMachine.ArchTests;

/// <summary>
/// У версии, которую собираются выпустить, есть запись выпуска.
/// </summary>
/// <remarks>
/// Выпуск собирается по тегу и берёт описание из <c>docs/RELEASES.md</c>; записи нет —
/// сборка выпуска падает. Это правильно, но поздно: тег к этому моменту уже опубликован,
/// а опубликованный тег переиспользовать нельзя (урок alpha.1). Здесь то же правило
/// проверяется на обычной сборке ветки — до переливки в master, пока номер ещё свободен.
/// </remarks>
public sealed class ReleaseNotesTests
{
    [Fact(DisplayName = "Выпуск: у текущей версии есть запись в RELEASES.md")]
    public void CurrentVersion_HasReleaseNotes()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "Directory.Build.props"));
        var version = Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value.Trim();

        Assert.False(string.IsNullOrEmpty(version), "В Directory.Build.props не найден <Version>.");

        // Предрелизное состояние выпуском не является: тег для него не создаётся.
        if (version == "0.0.0")
        {
            return;
        }

        var notes = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "docs", "RELEASES.md"));

        // Тот же разбор, что в release.yml: номер и пробел после него, иначе «0.2.0»
        // совпало бы с «0.2.0-alpha.3».
        var section = Regex.Match(
            notes,
            "^## " + Regex.Escape(version) + @"\s.*?(?=^---\s*$|^## |\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        Assert.True(
            section.Success,
            $"Версия {version} поднята, а раздела «## {version}» в docs/RELEASES.md нет. "
            + "Выпуск по тегу не соберётся, а тег к тому времени будет уже опубликован.");

        // Заголовок без текста — та же пустая страница выпуска, только с номером.
        Assert.True(
            section.Value.Split('\n').Count(line => line.Trim().Length > 0) > 3,
            $"Раздел «## {version}» в docs/RELEASES.md пуст: на странице выпуска будет один заголовок.");
    }
}
