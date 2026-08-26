using CommunityToolkit.Mvvm.ComponentModel;

namespace StormMachine.App.ViewModels;

/// <summary>Основа страницы раздела.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(NavigationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Section = section;
    }

    public NavigationSection Section { get; }

    public string Title => Section.Title;

    public string Route => Section.Route;

    public string Description => Section.Description;

    /// <summary>
    /// Вызывается при переходе на страницу.
    /// </summary>
    /// <remarks>
    /// Данные подгружаются при показе, а не при создании: собирать журнал прогонов
    /// для всех шестнадцати разделов на старте приложения — верный способ сделать
    /// запуск медленным без всякой пользы.
    /// </remarks>
    public virtual Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual void Deactivate()
    {
    }
}

/// <summary>Страница раздела, который ещё не сделан.</summary>
/// <remarks>
/// Недоступное не прячется, а объясняется — UX-принцип 6 (docs/01-analysis.md §9.5).
/// </remarks>
public sealed class PlaceholderPageViewModel(NavigationSection section) : PageViewModel(section)
{
    public string Availability => Section.Availability;

    public static string Explanation =>
        "Раздел показан заранее намеренно: недоступное не прячется, а объясняется. "
        + "Порядок итераций описан в docs/03-development-plan.md.";
}
