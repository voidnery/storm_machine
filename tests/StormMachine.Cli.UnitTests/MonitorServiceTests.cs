using StormMachine.Cli.Commands;

namespace StormMachine.Cli.UnitTests;

/// <summary>
/// Строка запуска службы мониторов.
/// </summary>
/// <remarks>
/// Проверяется именно она, потому что ошибка здесь не выглядит ошибкой: <c>sc</c>
/// создаёт службу успешно, а падает она при запуске — с сообщением о ненайденном файле,
/// в котором путь обрезан по первому пробелу. И продукт, и база лежат по путям
/// с пробелами: «C:\Program Files\…» и профиль пользователя.
/// <para>
/// Строка разбирается дважды: сначала <c>sc.exe</c>, потом диспетчером служб. Кавычки
/// нужны на обоих уровнях, и держать это в голове при каждой правке — плохая замена
/// проверке.
/// </para>
/// </remarks>
public sealed class MonitorServiceTests
{
    private const string Executable = @"C:\Program Files\Storm Machine\storm.exe";
    private const string Database = @"C:\Users\Иван Петров\AppData\Local\StormMachine\storm.db";

    [Fact]
    public void BinPath_QuotesBothPathsForTheServiceManager()
    {
        var binPath = MonitorServiceCommands.BuildBinPath(Executable, Database);

        // Внутренние кавычки экранированы для sc.exe: он снимает свой слой,
        // диспетчеру достаётся строка с обычными кавычками вокруг обоих путей.
        Assert.Contains($"\\\"{Executable}\\\"", binPath, StringComparison.Ordinal);
        Assert.Contains($"\\\"{Database}\\\"", binPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Путь к базе вписан в строку, а не оставлен на вычисление службе.
    /// </summary>
    /// <remarks>
    /// Это главное утверждение файла. Путь по умолчанию считается из профиля
    /// пользователя, а у службы профиль свой: под LocalSystem она открыла бы
    /// <c>C:\Windows\System32\config\systemprofile\…</c>, не нашла бы там ни одного
    /// монитора и вела бы себя в точности как исправная — работала бы и молчала.
    /// Отличить такую от настоящей нельзя ничем, кроме записанного пути.
    /// </remarks>
    [Fact]
    public void BinPath_CarriesTheDatabaseExplicitly()
    {
        var binPath = MonitorServiceCommands.BuildBinPath(Executable, Database);

        Assert.Contains("--база", binPath, StringComparison.Ordinal);
        Assert.Contains(Database, binPath, StringComparison.Ordinal);
    }

    /// <summary>Ключ службы стоит до ключа базы: его разбирают раньше разбора команд.</summary>
    [Fact]
    public void BinPath_PutsTheServiceSwitchBeforeTheDatabase()
    {
        var binPath = MonitorServiceCommands.BuildBinPath(Executable, Database);

        var service = binPath.IndexOf(MonitorServiceCommands.ServiceSwitch, StringComparison.Ordinal);
        var database = binPath.IndexOf("--база", StringComparison.Ordinal);

        Assert.True(service >= 0, "Ключ службы потерян — диспетчер запустит обычный клиент.");
        Assert.True(service < database);
    }

    /// <summary>
    /// Исполняемый файл идёт первым и целиком в кавычках.
    /// </summary>
    /// <remarks>
    /// Диспетчер служб берёт первым аргументом путь к файлу. Без кавычек
    /// «C:\Program Files\Storm Machine\storm.exe» превращается в «C:\Program»
    /// с аргументами — и это не выдуманная опасность, а самая частая ошибка
    /// установки служб на Windows.
    /// </remarks>
    [Fact]
    public void BinPath_StartsWithTheQuotedExecutable() =>
        Assert.StartsWith($"\\\"{Executable}\\\" ", MonitorServiceCommands.BuildBinPath(Executable, Database), StringComparison.Ordinal);

    /// <summary>Кириллица в пути пользователя не ломает строку.</summary>
    [Fact]
    public void BinPath_SurvivesCyrillicUserNames()
    {
        var binPath = MonitorServiceCommands.BuildBinPath(Executable, Database);

        Assert.Contains("Иван Петров", binPath, StringComparison.Ordinal);
    }

    [Fact]
    public void BinPath_HandlesPathsWithoutSpacesToo()
    {
        var binPath = MonitorServiceCommands.BuildBinPath(@"C:\storm\storm.exe", @"D:\db\storm.db");

        Assert.Contains(@"\""C:\storm\storm.exe\""", binPath, StringComparison.Ordinal);
        Assert.Contains(@"\""D:\db\storm.db\""", binPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Имя службы отличается от имени службы агента.
    /// </summary>
    /// <remarks>
    /// Обе могут стоять на одной машине: агент — точка измерения, монитор —
    /// наблюдение. Совпадение имён сделало бы установку второй молчаливой заменой
    /// первой.
    /// </remarks>
    [Fact]
    public void ServiceName_DoesNotClashWithTheAgent() =>
        Assert.NotEqual("StormAgent", MonitorServiceCommands.ServiceName);
}
