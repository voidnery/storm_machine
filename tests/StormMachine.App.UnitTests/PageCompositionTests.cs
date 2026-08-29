using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Каждая страница собирается из настоящего контейнера.
/// </summary>
/// <remarks>
/// Дыра, найденная в И-22 при добавлении раздела агентов: у страницы настроек появился
/// новый параметр конструктора, и ничто не проверяло, что контейнер умеет его выдать.
/// Забытая регистрация не ломает ни сборку, ни разметку — она ломается в тот момент,
/// когда оператор переходит на страницу, и выглядит как падение продукта на ровном месте.
/// <para>
/// Разметку проверяет компилятор XAML: у представлений задан <c>x:DataType</c>,
/// и опечатка в пути привязки роняет сборку. Регистрацию не проверял никто.
/// </para>
/// </remarks>
public sealed class PageCompositionTests
{
    // Контейнер освобождается асинхронно, как и в самом клиенте: планировщик мониторов
    // останавливает свой цикл и дожидается идущих проверок. Синхронное закрытие на нём
    // падает с прямым указанием использовать DisposeAsync — и это правильно, обрывать
    // проверку на полуслове значило бы потерять уже измеренное.

    /// <summary>
    /// Ни один раздел навигации не остаётся без работающей страницы.
    /// </summary>
    /// <remarks>
    /// Проверяются все разделы разом, а не перечисленные вручную: новый раздел должен
    /// ломать этот тест, если его страницу забыли зарегистрировать, — а не доезжать
    /// до оператора заглушкой.
    /// </remarks>
    [Fact]
    public async Task EverySection_BuildsItsPage()
    {
        await using var services = AppServices.Build();
        var factory = services.GetRequiredService<Func<NavigationSection, PageViewModel>>();

        var failures = new List<string>();

        foreach (var section in NavigationMap.Sections)
        {
            try
            {
                var page = factory(section);

                Assert.NotNull(page);
            }
            catch (Exception ex)
            {
                failures.Add($"{section.Route}: {ex.Message}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Страницы, которые контейнер не собрал:" + Environment.NewLine
            + string.Join(Environment.NewLine, failures));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Страница настроек собирается со всеми своими разделами.
    /// </summary>
    /// <remarks>
    /// Она собирает больше всех: возможности, профили, учётные данные SNMP, хранилище,
    /// обновление и — с И-22 — агентов. Каждый новый раздел добавляет ей зависимость,
    /// и именно она рискует больше прочих.
    /// </remarks>
    [Fact]
    public async Task SettingsPage_HasItsAgentsSection()
    {
        await using var services = AppServices.Build();
        var factory = services.GetRequiredService<Func<NavigationSection, PageViewModel>>();

        var section = NavigationMap.Sections.First(s => s.Route == NavigationMap.Settings);
        var page = Assert.IsType<SettingsPageViewModel>(factory(section));

        Assert.NotNull(page.Agents);
        Assert.NotNull(page.Editor);

        await Task.CompletedTask;
    }

    /// <summary>Оболочка собирается: без неё не открывается ни одна страница.</summary>
    [Fact]
    public async Task MainWindow_Builds()
    {
        await using var services = AppServices.Build();

        Assert.NotNull(services.GetRequiredService<MainWindowViewModel>());

        await Task.CompletedTask;
    }

    /// <summary>
    /// Каналы оповещения графического клиента зарегистрированы.
    /// </summary>
    /// <remarks>
    /// «Звук» и «уведомление» живут только здесь: монитор, заведённый с ними
    /// из консоли, честно предупреждает, что она ими оповещать не будет. Если бы
    /// их не оказалось и в клиенте, предупреждение стало бы неправдой в обе стороны.
    /// </remarks>
    [Fact]
    public async Task GuiAlertChannels_AreRegistered()
    {
        await using var services = AppServices.Build();

        var names = services
            .GetRequiredService<IEnumerable<Application.Abstractions.IAlertChannel>>()
            .Select(c => c.Name)
            .ToList();

        Assert.Contains("звук", names);
        Assert.Contains("уведомление", names);

        await Task.CompletedTask;
    }
}
