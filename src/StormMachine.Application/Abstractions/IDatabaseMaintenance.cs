namespace StormMachine.Application.Abstractions;

/// <summary>Итог проверки целостности файла базы.</summary>
public sealed record DatabaseHealth
{
    public required bool IsHealthy { get; init; }

    public required string DatabasePath { get; init; }

    /// <summary>Что именно не так — строки проверки целостности SQLite.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];
}

/// <summary>
/// Итог лечения базы.
/// </summary>
/// <remarks>
/// Отчёт называет потерянное явно. Лечение, которое молчит о потерях, оставляет
/// оператора с базой, где чего-то нет, и без знания, чего именно, — это хуже
/// самой потери.
/// </remarks>
public sealed record DatabaseRepairReport
{
    /// <summary>Куда убран повреждённый файл. Он не удаляется никогда.</summary>
    public required string BackupPath { get; init; }

    public required int RunsKept { get; init; }

    public required long SamplesKept { get; init; }

    /// <summary>
    /// Прогоны, оставшиеся без сырых сэмплов: агрегаты целы, сводка и отчёт работают,
    /// графика по сырью не будет — как после уборки по политике хранения.
    /// </summary>
    public required int RunsWithoutSamples { get; init; }

    /// <summary>Прогоны, потерянные целиком: от них остались только осиротевшие агрегаты.</summary>
    public required int RunsLost { get; init; }

    /// <summary>Таблицы, перенесённые не целиком: «имя: сколько строк удалось».</summary>
    public IReadOnlyList<string> PartialTables { get; init; } = [];
}

/// <summary>
/// Проверка и лечение файла базы.
/// </summary>
/// <remarks>
/// Повреждение файла — событие, к которому продукт обязан быть готов: диск, внезапное
/// отключение, чужой процесс. До И-24 оператор получал «SQLite Error 11: database disk
/// image is malformed» — язык механизма вместо языка задачи — и никакого способа
/// вылечиться, кроме удаления всей истории.
/// </remarks>
public interface IDatabaseMaintenance
{
    /// <summary>Полный путь к проверяемому файлу базы.</summary>
    string DatabasePath { get; }

    Task<DatabaseHealth> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Пересобирает базу: повреждённый файл уходит в резервную папку рядом,
    /// всё читаемое переносится в новый файл на его месте.
    /// </summary>
    Task<DatabaseRepairReport> RepairAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Перевод ошибок хранилища на язык задачи.
/// </summary>
/// <remarks>
/// Слою приложения запрещено ссылаться на инфраструктуру, поэтому исключение
/// опознаётся по имени типа и тексту, а не по типу: заворачивать каждый метод
/// каждого хранилища в трансляцию — рефакторинг несоразмерной цены за то же самое
/// поведение. Тексты SQLite не локализуются, сравнение по ним устойчиво.
/// </remarks>
public static class StorageProblem
{
    /// <summary>
    /// Понятное объяснение, если исключение похоже на повреждение файла базы;
    /// иначе <c>null</c> — показывать исходное сообщение.
    /// </summary>
    public static string? ExplainCorruption(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (!string.Equals(e.GetType().Name, "SqliteException", StringComparison.Ordinal))
            {
                continue;
            }

            if (e.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase))
            {
                return "файл базы повреждён и читается не весь. Проверить: storm db check; "
                     + "вылечить с резервной копией: storm db repair.";
            }
        }

        return null;
    }
}
