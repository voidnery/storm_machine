using System.Net.Http;
using System.Net.Sockets;

namespace StormMachine.App.Services;

/// <summary>
/// Сбой, которого страница не ждала, — человеческими словами.
/// </summary>
/// <remarks>
/// Страницы ловили те исключения, которые предвидели: неверный ввод, отсутствие
/// файла. Всё прочее уходило из команды в задачу, а из задачи — в тишину: кнопка
/// нажата, ничего не произошло, объяснения нет. Продукт, который меряет сеть,
/// обязан отвечать на «почему не сработало» первым, а не после расспросов.
///
/// Здесь не лечение, а перевод: тип исключения — оператору ни о чём, а «база
/// не открылась» или «сеть отказала» говорит, что делать дальше. Точный текст
/// системы остаётся в конце строки: он нужен тому, кто будет разбираться.
/// </remarks>
internal static class Trouble
{
    /// <summary>Одна строка для показа на странице.</summary>
    public static string Say(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return $"{Kind(exception)} {exception.Message} Подробности — в журнале клиента "
               + "(«Настройки» → «Журнал клиента»).";
    }

    private static string Kind(Exception exception) => exception switch
    {
        SocketException or HttpRequestException =>
            "Сеть отказала.",

        UnauthorizedAccessException =>
            "Нет доступа к файлу.",

        IOException =>
            "Файл недоступен.",

        // Тип исключения SQLite приходит из инфраструктуры, ссылаться на неё клиенту
        // нельзя — это проверяет архитектурный тест. Поэтому распознаём по имени:
        // хрупко, но честнее, чем показать оператору «SqliteException» и уйти.
        _ when exception.GetType().Name.StartsWith("Sqlite", StringComparison.Ordinal) =>
            "База измерений не открылась. Снимите «В журнал», чтобы померить без записи, "
            + "и проверьте базу в «Настройках».",

        _ => "Непредвиденный сбой.",
    };
}
