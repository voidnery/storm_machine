using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Application.Probes;

/// <summary>Тип параметра пробы. По нему интерфейс строит поле ввода.</summary>
public enum ProbeParameterType
{
    Integer,
    Decimal,
    Duration,
    Text,
    Boolean,
    Choice,
}

/// <summary>
/// Объявление одного параметра пробы.
/// </summary>
/// <remarks>
/// Ключевая идея принципа 1 (docs/01-analysis.md §8.2): проба описывает свои параметры
/// декларативно, а интерфейс строит форму по описанию. Поэтому новая проба не требует
/// правки UI — иначе каждый «дополнительный тест» переписывал бы приложение.
/// </remarks>
public sealed record ProbeParameter
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public required ProbeParameterType Type { get; init; }

    public object? DefaultValue { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    /// <summary>Допустимые значения для <see cref="ProbeParameterType.Choice"/>.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    public string? Description { get; init; }

    public bool IsRequired { get; init; }
}

/// <summary>Паспорт пробы: чем она меряет, в чём и по какой методике.</summary>
public sealed record ProbeDescriptor
{
    public required ProbeKind Kind { get; init; }

    /// <summary>Форма результата — по ней строится показ и схема хранения.</summary>
    public ProbeResultShape Shape { get; init; } = ProbeResultShape.ScalarSeries;

    /// <summary>
    /// Как назвать ряды сравнения в заголовке таблицы: «резолвер», «фаза».
    /// </summary>
    /// <remarks>
    /// Форма <c>ComparedSeries</c> появилась под сравнение резолверов, и показ был написан
    /// его словами. С приходом задержки под нагрузкой это вылезло: измерение очередей
    /// подписывалось «Резолвер», а строка «быстрее всех отвечает без нагрузки» сообщала
    /// очевидное как открытие. Слово знает проба — она и называет.
    /// </remarks>
    public string? SeriesNoun { get; init; }

    /// <summary>
    /// Ряды — взаимозаменяемые варианты, а не фазы одного измерения.
    /// </summary>
    /// <remarks>
    /// От этого зависит, осмысленно ли «быстрее всех». Для пяти резолверов — да,
    /// это и есть ответ. Для «без нагрузки» и «под нагрузкой» — нет: то, что под
    /// нагрузкой медленнее, и составляет предмет измерения, а не его вывод.
    /// </remarks>
    public bool SeriesAreAlternatives { get; init; }

    /// <summary>
    /// Пробе нужна цель, названная оператором.
    /// </summary>
    /// <remarks>
    /// Не всякая проба измеряет до того, что назвали. Скорость наружу спрашивает
    /// сервер у службы выбора: у M-Lab есть данные о загрузке узлов, которых у нас нет,
    /// и требовать от оператора вписать сервер значило бы заставить его гадать. Такая
    /// проба цели не имеет, и просить её у человека нельзя.
    /// </remarks>
    public bool RequiresTarget { get; init; } = true;

    /// <summary>Имя для командной строки: <c>ping</c>, <c>tcp</c>, <c>dns</c>.</summary>
    public required string Name { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required MeasurementUnit Unit { get; init; }

    public required Methodology Methodology { get; init; }

    public required IReadOnlyList<ProbeParameter> Parameters { get; init; }

    /// <summary>
    /// Нужны ли повышенные права. Уровень 0 обходится без них — это проверено
    /// (docs/02-research.md, R-01).
    /// </summary>
    public bool RequiresElevation { get; init; }

    /// <summary>
    /// Нужна ли вторая точка измерения — сопряжённый агент.
    /// </summary>
    /// <remarks>
    /// Объявляется пробой, а не выводится по её виду. Выводить было бы неверно:
    /// <c>iperf3</c> тоже меряет пропускную способность, но агента не требует —
    /// он ходит к чужому серверу. Требование знает только сама проба.
    /// </remarks>
    public bool RequiresAgent { get; init; }
}
