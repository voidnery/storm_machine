using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.App.ViewModels;
using StormMachine.Application.Abstractions;
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

        // Каналы, у которых есть смысл только при живом окне. Корень композиции
        // их не регистрирует по той же причине, по которой консоль регистрирует свой.
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<IAlertChannel, BannerAlertChannel>();
        services.AddSingleton<IAlertChannel, SoundAlertChannel>();
        services.AddSingleton<TrayIndicator>();
        services.AddSingleton<UpdateService>();

        services.AddSingleton<FilePicker>();
        services.AddSingleton<IFilePicker>(p => p.GetRequiredService<FilePicker>());
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
        NavigationMap.Path => ActivatorUtilities.CreateInstance<PathPageViewModel>(provider, section),
        NavigationMap.Discovery => ActivatorUtilities.CreateInstance<DiscoveryPageViewModel>(provider, section),
        NavigationMap.Devices => ActivatorUtilities.CreateInstance<DevicesPageViewModel>(provider, section),
        NavigationMap.Topology => ActivatorUtilities.CreateInstance<TopologyPageViewModel>(provider, section),
        NavigationMap.Presets => ActivatorUtilities.CreateInstance<PresetsPageViewModel>(provider, section),
        NavigationMap.Runs => ActivatorUtilities.CreateInstance<RunsPageViewModel>(provider, section),
        NavigationMap.Probes => ActivatorUtilities.CreateInstance<ProbesPageViewModel>(provider, section),
        NavigationMap.Inspect => ActivatorUtilities.CreateInstance<InspectPageViewModel>(provider, section),
        NavigationMap.Monitors => ActivatorUtilities.CreateInstance<MonitorsPageViewModel>(provider, section),
        NavigationMap.Schedule => ActivatorUtilities.CreateInstance<SchedulePageViewModel>(provider, section),
        NavigationMap.Alerts => ActivatorUtilities.CreateInstance<AlertsPageViewModel>(provider, section),
        NavigationMap.Reports => ActivatorUtilities.CreateInstance<ReportsPageViewModel>(provider, section),
        NavigationMap.Settings => ActivatorUtilities.CreateInstance<SettingsPageViewModel>(provider, section),
        NavigationMap.LocalTests => ActivatorUtilities.CreateInstance<LocalTestsPageViewModel>(provider, section),
        NavigationMap.Speed => ActivatorUtilities.CreateInstance<SpeedPageViewModel>(provider, section),
        NavigationMap.Development => ActivatorUtilities.CreateInstance<DevelopmentPageViewModel>(provider, section),
        _ => new PlaceholderPageViewModel(section),
    };
}
