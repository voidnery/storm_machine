using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Application.Abstractions;

/// <summary>Что нужно знать хранилищу, чтобы открыть запись прогона.</summary>
public sealed record RunDescriptor
{
    public required ProbeKind Kind { get; init; }

    public required string ProbeName { get; init; }

    public required ProbeResultShape Shape { get; init; }

    public required Target Target { get; init; }

    public required MeasurementContext Context { get; init; }

    public required MeasurementUnit Unit { get; init; }

    /// <summary>
    /// Значения параметров пробы.
    /// </summary>
    /// <remarks>
    /// Сохраняются вместе с прогоном: без них результат нельзя ни повторить, ни истолковать.
    /// Ping на 4 пробы и ping на 10 000 — разные измерения, а в журнале выглядели бы одинаково.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пресет, из которого запущен прогон, если запуск шёл из библиотеки.</summary>
    public Guid? PresetId { get; init; }

    /// <summary>
    /// Редакция пресета на момент запуска.
    /// </summary>
    /// <remarks>
    /// Нужна, чтобы было видно: результат получен пресетом второй редакции, а в библиотеке
    /// сейчас пятая — сравнивать их напрямую нельзя. Историю редакций хранить не требуется:
    /// фактические параметры лежат в самом прогоне.
    /// </remarks>
    public int? PresetVersion { get; init; }
}

/// <summary>
/// Запись одного прогона.
/// </summary>
/// <remarks>
/// Сэмплы пишутся <b>по ходу</b>, а не одним куском в конце. Это прямое следствие
/// требования отказоустойчивости: прерванный прогон обязан сохранить измеренное,
/// а прогон, оборванный падением процесса, — не превратиться в пустую строку журнала.
/// </remarks>
public interface IRunWriter : IAsyncDisposable
{
    Guid RunId { get; }

    /// <summary>Добавляет сэмпл. Запись на диск может быть отложена до накопления пачки.</summary>
    ValueTask AppendAsync(Sample sample, CancellationToken cancellationToken = default);

    /// <summary>Подводит итог: досбрасывает сэмплы, считает агрегаты, сохраняет факты.</summary>
    Task CompleteAsync(
        IReadOnlyList<ProbeFact> facts,
        string? resolvedAddress,
        bool wasCancelled,
        CancellationToken cancellationToken = default);
}

/// <summary>Хранилище прогонов.</summary>
/// <summary>
/// Как снаружи указать другой файл базы.
/// </summary>
/// <remarks>
/// Живёт в слое приложения, потому что имя переменной нужно и хранилищу, и клиентам:
/// консоль объявляет одноимённый ключ, а ссылаться на инфраструктуру ей запрещено
/// архитектурным правилом.
/// </remarks>
public static class StorageEnvironment
{
    /// <summary>Переменная окружения с путём к файлу базы.</summary>
    public const string PathVariable = "STORM_DB";
}

/// <summary>
/// Где продукт держит свои данные.
/// </summary>
/// <remarks>
/// Показывается оператору не для полноты. Когда данные ведут себя не так, как ожидалось —
/// сопряжение пропало, журнал пуст, — первый вопрос всегда один: с каким файлом мы вообще
/// разговариваем. Продукт, который не может на него ответить, оставляет человека
/// разбираться догадками.
/// </remarks>
public interface IStorageLocation
{
    /// <summary>Полный путь к файлу базы.</summary>
    string DatabasePath { get; }
}

public interface IRunStore
{
    /// <summary>Путь к файлу базы — показывается оператору в настройках и в диагностике.</summary>
    string Location { get; }

    /// <summary>Создаёт или обновляет схему и помечает прогоны, оставшиеся открытыми после сбоя.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IRunWriter> BeginRunAsync(RunDescriptor descriptor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunSummary>> ListAsync(RunQuery query, CancellationToken cancellationToken = default);

    Task<StoredRun?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Применяет политику хранения: удаляет старые сэмплы и совсем старые прогоны.</summary>
    Task<RetentionReport> ApplyRetentionAsync(
        RetentionPolicy policy,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    /// <summary>Размер файла базы в байтах и число прогонов — для показа в настройках.</summary>
    Task<(long SizeBytes, int RunCount, long SampleCount)> GetUsageAsync(CancellationToken cancellationToken = default);
}
