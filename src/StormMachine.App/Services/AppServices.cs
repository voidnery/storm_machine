using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.App.ViewModels;
using StormMachine.Composition;

namespace StormMachine.App.Services;

/// <summary>
/// Сборка служб графического клиента.
/// </summary>
/// <remarks>
/// Ядро собирается тем же вызовом <c>AddStormMachine()</c>, что и в консоли: клиент
/// добавляет к нему только своё — службу выполняющихся прогонов и модели представления.
/// Ссылок на инфраструктуру здесь нет, и архитектурный тест это проверяет.
/// </remarks>
internal static class AppServices
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddStormMachine();

        services.AddSingleton<RunnerService>();
        services.AddSingleton<MainWindowViewModel>();

        // Страницы создаются по разделу: раздел знает свой путь, фабрика — чем его наполнить.
        services.AddSingleton<Func<NavigationSection, PageViewModel>>(provider => section =>
            CreatePage(provider, section));

        return services.BuildServiceProvider();
    }

    private static PageViewModel CreatePage(IServiceProvider provider, NavigationSection section) => section.Route switch
    {
        NavigationMap.Dashboard => ActivatorUtilities.CreateInstance<DashboardPageViewModel>(provider, section),
        NavigationMap.Latency => ActivatorUtilities.CreateInstance<LatencyPageViewModel>(provider, section),
        NavigationMap.Runs => ActivatorUtilities.CreateInstance<RunsPageViewModel>(provider, section),
        _ => new PlaceholderPageViewModel(section),
    };
}
