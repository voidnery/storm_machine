using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;

// Классы этой сборки идут по одному. Она поднимает целый оконный движок с состоянием
// на весь процесс — поток разметки, диспетчер, словарь ресурсов приложения, — и рядом,
// в другой коллекции, собирает контейнер со всеми страницами. Параллельно эти две
// работы сталкивались: на общем раннере настройка headless-приложения падала
// с «объектом владеет другой поток» у того теста, который дошёл до сессии первым,
// а следующие проходили — то есть состояние не портилось, сталкивались именно потоки.
//
// Плата — ноль: тридцать шесть проверок идут те же три секунды. Коллекции для этого
// мало: её надо не забыть указать каждому новому классу, а забыть легко — падает
// оно раз в несколько прогонов и только на медленной машине.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StormMachine.App.UnitTests;

/// <summary>
/// Сборка настоящего приложения для headless-прогона.
/// </summary>
/// <remarks>
/// Берётся именно <see cref="App" />, а не заглушка: страницы ссылаются на кисти
/// и стили из App.axaml, и подделка ресурсов проверяла бы подделку. Побочных
/// действий у App при этом нет — окно, планировщик и трей поднимаются только
/// под desktop-lifetime, которого в headless-прогоне не существует.
/// </remarks>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Одна headless-сессия на весь прогон.
/// </summary>
/// <remarks>
/// Платформа Avalonia инициализируется в процессе один раз — вторая сессия упала бы.
/// Тесты, которым нужен UI-поток, входят в коллекцию <c>Headless</c> и выполняют
/// код через <see cref="Session" />.Dispatch.
/// </remarks>
[CollectionDefinition("Headless")]
public sealed class HeadlessTests : ICollectionFixture<HeadlessSessionFixture>;

public sealed class HeadlessSessionFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } =
        HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    public void Dispose() => Session.Dispose();
}

/// <summary>Изоляция тестов от рабочей истории оператора.</summary>
internal static class TestEnvironment
{
    /// <summary>
    /// Правило проекта: проверки — в отдельную базу.
    /// </summary>
    /// <remarks>
    /// Тесты клиента собирают настоящий контейнер, и без этой подмены фоновая
    /// проверка целостности в оболочке читала бы рабочую базу того, кто запустил
    /// тесты. Инициализатор модуля срабатывает до любого теста и до чтения
    /// переменной чем бы то ни было.
    /// </remarks>
    [ModuleInitializer]
    public static void UseIsolatedDatabase() =>
        Environment.SetEnvironmentVariable(
            "STORM_DB",
            Path.Combine(Path.GetTempPath(), "storm-tests", $"app-{Guid.NewGuid():N}.db"));
}
