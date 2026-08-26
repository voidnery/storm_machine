namespace StormMachine.Domain.Measurements;

/// <summary>Чем закончилась отдельная проба.</summary>
public enum SampleStatus
{
    Success,
    Timeout,
    Unreachable,

    /// <summary>Истёк TTL — промежуточный узел на маршруте (используется в traceroute).</summary>
    TtlExpired,

    /// <summary>Ответ получен, но не прошёл проверку содержимого или сертификата.</summary>
    Rejected,

    Error,
}

/// <summary>Единица измерения. Хранится в результате, а не в каждом сэмпле.</summary>
public enum MeasurementUnit
{
    Milliseconds,
    MegabitsPerSecond,
    Percent,
    Bytes,
    Count,
}

/// <summary>
/// Одно измерение. Структура, а не класс: сэмплов десятки тысяч, и они не должны
/// нагружать сборщик мусора в горячем пути (принцип 9 из docs/01-analysis.md §8.2).
/// </summary>
public readonly record struct Sample
{
    /// <summary>Порядковый номер в серии — по нему считается потеря и переупорядочивание.</summary>
    public required int Sequence { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Измеренная величина в единицах результата. Для неуспешных сэмплов — <see cref="double.NaN"/>.
    /// </summary>
    public required double Value { get; init; }

    public required SampleStatus Status { get; init; }

    /// <summary>Кто ответил, если это не совпадает с целью (например, хоп в traceroute).</summary>
    public string? RespondedBy { get; init; }

    /// <summary>TTL в ответе — грубый признак операционной системы и числа хопов.</summary>
    public int? Ttl { get; init; }

    /// <summary>
    /// Имя составляющей измерения: фаза HTTP (<c>dns</c>, <c>connect</c>, <c>tls</c>,
    /// <c>ttfb</c>, <c>download</c>), тип запроса DNS, имя резолвера.
    /// </summary>
    /// <remarks>
    /// Добавлено в И-2. Оказалось, что не всякое измерение — точка в одномерном ряду:
    /// один HTTP-запрос даёт пять длительностей, и они не следуют друг за другом
    /// во времени как независимые пробы, а раскладывают одно событие на фазы.
    /// Для ICMP, TCP и UDP остаётся <c>null</c>, и структура не тяжелеет.
    /// </remarks>
    public string? Label { get; init; }

    /// <summary>
    /// Номер группы, к которой относится сэмпл: хоп в traceroute, попытка в серии запросов.
    /// </summary>
    /// <remarks>
    /// Второе измерение, которого не было в исходной модели. Traceroute даёт матрицу
    /// «хоп × попытка», а не ряд: без группировки данные о потерях на конкретном хопе
    /// восстановить нельзя.
    /// </remarks>
    public int? Group { get; init; }

    public bool IsSuccess => Status == SampleStatus.Success;

    public static Sample Ok(int sequence, DateTimeOffset timestampUtc, double value) => new()
    {
        Sequence = sequence,
        TimestampUtc = timestampUtc,
        Value = value,
        Status = SampleStatus.Success,
    };

    public static Sample Failed(int sequence, DateTimeOffset timestampUtc, SampleStatus status) => new()
    {
        Sequence = sequence,
        TimestampUtc = timestampUtc,
        Value = double.NaN,
        Status = status,
    };
}
