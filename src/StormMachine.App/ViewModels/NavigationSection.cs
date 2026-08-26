namespace StormMachine.App.ViewModels;

/// <summary>
/// Раздел навигации. Структура повторяет карту экранов из docs/01-analysis.md §9.1.
/// </summary>
/// <param name="Route">Путь раздела — он же будущий адрес в web-варианте.</param>
/// <param name="Title">Название в боковом меню.</param>
/// <param name="Description">Что здесь будет.</param>
/// <param name="Iteration">Итерация, в которой раздел появится. <c>null</c> — уже готов.</param>
public sealed record NavigationSection(
    string Route,
    string Title,
    string Description,
    string? Iteration)
{
    public bool IsReady => Iteration is null;

    /// <summary>
    /// Подпись о готовности. Недоступное не прячется, а показывается с пояснением —
    /// UX-принцип 6 из docs/01-analysis.md §9.5.
    /// </summary>
    public string Availability => IsReady ? "готово" : $"появится в {Iteration}";
}
