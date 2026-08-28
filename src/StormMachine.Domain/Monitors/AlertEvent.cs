using StormMachine.Domain.Results;

namespace StormMachine.Domain.Monitors;

/// <summary>
/// Запись в ленте алертов.
/// </summary>
/// <remarks>
/// Пишется на каждую смену состояния, <b>включая ту, о которой промолчали</b>. Пауза
/// между оповещениями гасит сообщение в канале, но не событие: иначе лента показывала бы
/// сеть исправной ровно в те минуты, когда продукт решил не шуметь.
/// </remarks>
public sealed record AlertEvent
{
    public required Guid Id { get; init; }

    public required Guid MonitorId { get; init; }

    /// <summary>Имя монитора на момент события — переименование не должно ломать историю.</summary>
    public required string MonitorName { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    public required AlertAction Action { get; init; }

    public VerdictLevel Level { get; init; } = VerdictLevel.Unknown;

    /// <summary>Почему состояние сменилось — формулировка из правила.</summary>
    public required string Reason { get; init; }

    /// <summary>Что сказала сама проверка.</summary>
    public string? Summary { get; init; }

    /// <summary>Проверка, на которой событие произошло.</summary>
    public Guid? CheckId { get; init; }

    /// <summary>Дошло ли до каналов. Ложь означает «событие было, шуметь не стали».</summary>
    public bool Notified { get; init; }

    /// <summary>Каналы, в которые ушло сообщение.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];

    /// <summary>Каналы, которые не смогли доставить, и почему.</summary>
    /// <remarks>
    /// Молчащий канал опаснее отсутствующего: на него рассчитывают. Ошибка доставки
    /// хранится рядом с событием, чтобы «нам не пришло письмо» имело ответ.
    /// </remarks>
    public IReadOnlyList<string> DeliveryErrors { get; init; } = [];

    public string ActionText => Action switch
    {
        AlertAction.Raised => "поднят",
        AlertAction.Cleared => "снят",
        AlertAction.Repeated => "напоминание",
        _ => "без изменений",
    };
}

/// <summary>Фильтр для ленты алертов.</summary>
public sealed record AlertQuery
{
    public Guid? MonitorId { get; init; }

    public DateTimeOffset? Since { get; init; }

    /// <summary>Только те, о которых оповещали.</summary>
    public bool NotifiedOnly { get; init; }

    public int Limit { get; init; } = 200;
}
