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
        PresetsPageViewModel => new PresetsPage(),
        RunsPageViewModel => new RunsPage(),
        PlaceholderPageViewModel => new PlaceholderPage(),
        _ => new TextBlock { Text = $"Нет представления для {param?.GetType().Name ?? "null"}" },
    };

    public bool Match(object? data) => data is PageViewModel;
}
