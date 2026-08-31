namespace StormMachine.App.ViewModels;

/// <summary>
/// Раздел навигации. Структура повторяет карту экранов из docs/01-analysis.md §9.1.
/// </summary>
/// <param name="Route">Путь раздела — он же будущий адрес в web-варианте.</param>
/// <param name="Title">Название в боковом меню.</param>
/// <param name="Description">Что здесь есть.</param>
/// <param name="ConsoleCommands">
/// Чем то же самое делается из консоли, пока у раздела нет экранной формы.
/// <c>null</c> — экранная форма готова. Раньше здесь было поле «итерация, в которой
/// раздел появится» — и оно молча устарело ровно так, как поле <c>Iteration</c>
/// у возможности (урок 9 в STATUS.md): «появится в И-13» висело одиннадцать
/// итераций после И-13. Обещание срока заменено фактом, который сверяется
/// проверкой: названные команды обязаны существовать в продукте.
/// </param>
public sealed record NavigationSection(
    string Route,
    string Title,
    string Description,
    string? ConsoleCommands)
{
    public bool IsReady => ConsoleCommands is null;

    /// <summary>
    /// Подпись о готовности. Недоступное не прячется, а объясняется —
    /// UX-принцип 6 из docs/01-analysis.md §9.5.
    /// </summary>
    public string Availability => IsReady ? "готово" : "есть в консоли, экранной формы пока нет";
}
