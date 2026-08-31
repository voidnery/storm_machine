using Avalonia.Controls;
using Avalonia.Controls.Templates;
using StormMachine.App.ViewModels;
using StormMachine.App.Views.Pages;

namespace StormMachine.App;

/// <summary>
/// Сопоставление модели представления и представления.
/// </summary>
/// <remarks>
/// Сопоставление явное, а не по соглашению об именах через рефлексию: клиент публикуется
/// с обрезкой неиспользуемого кода, и поиск типа по строке имени при обрезке молча
/// перестал бы находить представления — причём у пользователя, а не при сборке.
/// </remarks>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? param) => param switch
    {
        DashboardPageViewModel => new DashboardPage(),
        LatencyPageViewModel => new LatencyPage(),
        DevicesPageViewModel => new DevicesPage(),
        DiscoveryPageViewModel => new DiscoveryPage(),
        PathPageViewModel => new PathPage(),
        PresetsPageViewModel => new PresetsPage(),
        TopologyPageViewModel => new TopologyPage(),
        RunsPageViewModel => new RunsPage(),
        ProbesPageViewModel => new ProbesPage(),
        InspectPageViewModel => new InspectPage(),
        MonitorsPageViewModel => new MonitorsPage(),
        SchedulePageViewModel => new SchedulePage(),
        AlertsPageViewModel => new AlertsPage(),
        ReportsPageViewModel => new ReportsPage(),
        SettingsPageViewModel => new SettingsPage(),
        LocalTestsPageViewModel => new LocalTestsPage(),
        SpeedPageViewModel => new SpeedPage(),
        DevelopmentPageViewModel => new DevelopmentPage(),
        PlaceholderPageViewModel => new PlaceholderPage(),
        _ => new TextBlock { Text = $"Нет представления для {param?.GetType().Name ?? "null"}" },
    };

    public bool Match(object? data) => data is PageViewModel;
}
