using StormMachine.Domain.Measurements;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.Results;

/// <summary>Чем закончился прогон.</summary>
public enum RunState
{
    /// <summary>Запись открыта, прогон идёт или процесс завершился до записи итога.</summary>
    Running = 0,

    Completed = 1,

    /// <summary>Прерван оператором. Измеренное до прерывания сохранено.</summary>
    Cancelled = 2,

    /// <summary>
    /// Запись осталась открытой: процесс завершился аварийно.
    /// </summary>
    /// <remarks>
    /// Выставляется при следующем открытии хранилища. Прогон не теряется — сэмплы,
    /// записанные до сбоя, остаются доступными; помечается лишь то, что итог не подводился.
    /// </remarks>
    Abandoned = 3,
}

/// <summary>Строка журнала прогонов: то, что нужно для списка, без сырых данных.</summary>
public sealed record RunSummary
{
    public required Guid Id { get; init; }

    public required ProbeKind Kind { get; init; }

    public required string ProbeName { get; init; }

    public required ProbeResultShape Shape { get; init; }

    public required string TargetDisplay { get; init; }

    public string? ResolvedAddress { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public required RunState State { get; init; }

    public required int SentCount { get; init; }

    public required int SuccessCount { get; init; }

    /// <summary>Медиана по всему прогону — одна цифра для строки списка.</summary>
    public double? MedianMs { get; init; }

    public required bool HasRawSamples { get; init; }

    /// <summary>Пресет, из которого запущен прогон.</summary>
    public Guid? PresetId { get; init; }

    /// <summary>Редакция пресета на момент запуска.</summary>
    public int? PresetVersion { get; init; }

    public int LostCount => SentCount - SuccessCount;

    public double LossPercent => SentCount == 0 ? 0 : LostCount * 100.0 / SentCount;

    public TimeSpan? Duration => CompletedUtc is { } completed ? completed - StartedUtc : null;
}

/// <summary>Прогон целиком: сводка, агрегаты по рядам, факты и (если сохранились) сырые сэмплы.</summary>
public sealed record StoredRun
{
    public required RunSummary Summary { get; init; }

    public required MeasurementContext Context { get; init; }

    public required MeasurementUnit Unit { get; init; }

    public required Target Target { get; init; }

    public required IReadOnlyList<SeriesStatistics> Series { get; init; }

    public required IReadOnlyList<ProbeFact> Facts { get; init; }

    /// <summary>
    /// Сырые сэмплы. Пусто, если политика хранения их уже удалила.
    /// </summary>
    /// <remarks>
    /// Пустой список здесь означает не «измерений не было», а «подробности состарились».
    /// Различать эти случаи нужно по <see cref="RunSummary.HasRawSamples"/>, иначе
    /// старый прогон будет выглядеть как неудачный.
    /// </remarks>
    public required IReadOnlyList<Sample> Samples { get; init; }

    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Фильтр для списка прогонов.</summary>
public sealed record RunQuery
{
    public int Limit { get; init; } = 20;

    public string? ProbeName { get; init; }

    public bool OnlyFailed { get; init; }

    public DateTimeOffset? Since { get; init; }

    /// <summary>Только прогоны, запущенные из указанного пресета.</summary>
    public Guid? PresetId { get; init; }
}

/// <summary>
/// Политика хранения.
/// </summary>
/// <remarks>
/// Обязательна с первого дня, а не «когда понадобится»: временные ряды растут линейно
/// и без ограничения превращают файл базы в проблему за месяцы. Удаляются сырые сэмплы,
/// агрегаты остаются — история и отчёты продолжают работать.
/// </remarks>
public sealed record RetentionPolicy
{
    /// <summary>Сколько хранить сырые сэмплы.</summary>
    public TimeSpan RawSampleHorizon { get; init; } = TimeSpan.FromDays(90);

    /// <summary>Сколько хранить сами прогоны с агрегатами.</summary>
    public TimeSpan RunHorizon { get; init; } = TimeSpan.FromDays(365);

    public static readonly RetentionPolicy Default = new();
}

/// <summary>Что сделала уборка.</summary>
public sealed record RetentionReport
{
    public required int RunsDeleted { get; init; }

    public required int RunsDownsampled { get; init; }

    public required long SamplesDeleted { get; init; }

    public bool IsEmpty => RunsDeleted == 0 && RunsDownsampled == 0 && SamplesDeleted == 0;
}
