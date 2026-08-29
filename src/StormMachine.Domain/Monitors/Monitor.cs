using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.Monitors;

/// <summary>Что монитор запускает.</summary>
public enum MonitorKind
{
    /// <summary>Одну пробу.</summary>
    Probe,

    /// <summary>Сценарий из цепочки шагов.</summary>
    Scenario,

    /// <summary>
    /// Загрузку и ошибки порта оборудования по SNMP.
    /// </summary>
    /// <remarks>
    /// Появился в И-21. До него монитор умел только пробы и сценарии, то есть наблюдал
    /// сеть <b>снаружи</b> — со своей машины и своими пакетами. Счётчики порта отвечают
    /// на другой вопрос: что происходит на самом оборудовании. Растущий счётчик ошибок
    /// находит умирающий патч-корд раньше, чем это заметит любая проба: пакеты ещё
    /// доходят, повторная передача их вытягивает, и снаружи канал выглядит просто
    /// чуть медленнее.
    /// </remarks>
    PortLoad,

    /// <summary>
    /// Появление новых серверов DHCP в сегменте.
    /// </summary>
    /// <remarks>
    /// Тоже И-21. Единственный монитор, который наблюдает не за величиной, а за
    /// <b>появлением</b>: два сервера DHCP в одном домене бывают и законно, и вердикта
    /// об этом продукт не выносит. Событие здесь другое и проверяемое — сервер,
    /// которого раньше не слышали, или знакомый сервер, начавший объявлять другой шлюз.
    /// </remarks>
    Dhcp,
}

/// <summary>Чем оказалась проверка в журнале доступности.</summary>
/// <remarks>
/// Три состояния, а не два, и это принципиально. «Измерено» — мы наблюдали.
/// «Пропущено» — машина не работала, и о сети в это время нам не известно ничего.
/// «Обслуживание» — мы сами не проверяли, потому что шли плановые работы.
/// Свести последние два к «работало» значит завысить доступность, к «не работало» —
/// занизить. Оба ответа были бы неправдой, и потому у них своя пометка.
/// </remarks>
public enum CheckKind
{
    Measured,

    Missed,

    Maintenance,
}

/// <summary>
/// Монитор: проверка, повторяющаяся по расписанию, с порогами и оценкой доступности.
/// </summary>
/// <remarks>
/// Своих измерений не делает — запускает те же пробы и те же сценарии, что и ручной
/// запуск, через тот же оркестратор. Поэтому каждая проверка попадает в журнал обычным
/// прогоном и открывается в отчёте как любой другой: у монитора нет отдельной, «своей»
/// правды о сети.
/// </remarks>
public sealed record Monitor
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public MonitorKind Kind { get; init; } = MonitorKind.Probe;

    /// <summary>Имя пробы или ключ шаблона сценария.</summary>
    public required string Subject { get; init; }

    public required Target Target { get; init; }

    /// <summary>
    /// Параметры пробы строками.
    /// </summary>
    /// <remarks>
    /// Строками — как в пресете, и по той же причине: их набор объявляет сама проба,
    /// и хранилищу незачем знать, что у ICMP есть <c>ttl</c>, а у HTTP — <c>method</c>.
    /// </remarks>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Пороги, по которым ставится вердикт проверки.
    /// </summary>
    /// <remarks>
    /// У сценария свои пороги в шагах, и эти дополняют их на уровне монитора.
    /// Пусто — вердикт <see cref="VerdictLevel.Unknown"/>: монитор без порогов
    /// собирает историю, но ни о чём не судит и никого не будит.
    /// </remarks>
    public IReadOnlyList<Threshold> Thresholds { get; init; } = [];

    public required Schedule Schedule { get; init; }

    /// <summary>Правило оповещения. Пусто — монитор пишет историю и молчит.</summary>
    public AlertRule? Alert { get; init; }

    /// <summary>Цель по доступности. Пусто — доступность считается, но ни с чем не сравнивается.</summary>
    public ServiceLevelObjective? Objective { get; init; }

    /// <summary>Пресет, из которого монитор заведён, — чтобы видеть родство настроек.</summary>
    public Guid? PresetId { get; init; }

    public bool IsEnabled { get; init; } = true;

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Когда монитор должен сработать в следующий раз.
    /// </summary>
    /// <remarks>
    /// Хранится в базе, а не вычисляется при запуске из «сейчас». Это и есть то,
    /// что позволяет расписанию пережить перезапуск и сон: после включения продукт
    /// видит назначенный срок в прошлом и знает, сколько именно пропущено.
    /// </remarks>
    public DateTimeOffset? NextDueUtc { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} — {Description}";

    /// <summary>Проверяет монитор целиком: расписание, пороги, правило алерта.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("У монитора должно быть имя.");
        }

        errors.AddRange(Schedule.Validate());

        if (Alert is not null)
        {
            errors.AddRange(Alert.Validate());

            if (Thresholds.Count == 0 && Kind == MonitorKind.Probe)
            {
                errors.Add(
                    "Задано правило оповещения, но нет ни одного порога: судить не по чему, "
                    + "и алерт не сработает никогда.");
            }
        }

        if (Objective is not null)
        {
            errors.AddRange(Objective.Validate());
        }

        return errors;
    }
}

/// <summary>
/// Текущее состояние монитора.
/// </summary>
/// <remarks>
/// Отдельно от определения намеренно: определение задаёт человек, состояние набегает
/// само. Смешать их значило бы поднимать «изменён оператором» на каждой проверке.
/// </remarks>
public sealed record MonitorStatus
{
    public static readonly MonitorStatus Fresh = new();

    public VerdictLevel Level { get; init; } = VerdictLevel.Unknown;

    public DateTimeOffset? LastRunUtc { get; init; }

    public string? LastSummary { get; init; }

    /// <summary>Состояние оповещения — счётчики гистерезиса и время подъёма.</summary>
    public AlertState Alert { get; init; } = AlertState.Clear;
}

/// <summary>Одна выполненная (или не выполненная) проверка.</summary>
public sealed record MonitorCheck
{
    public required Guid Id { get; init; }

    public required Guid MonitorId { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public TimeSpan Duration { get; init; }

    public CheckKind Kind { get; init; } = CheckKind.Measured;

    public VerdictLevel Level { get; init; } = VerdictLevel.Unknown;

    public required string Summary { get; init; }

    /// <summary>Прогон в журнале, если проверка что-то измерила.</summary>
    public Guid? RunId { get; init; }

    public string? Metric { get; init; }

    public double? Value { get; init; }

    public double? Threshold { get; init; }

    /// <summary>Сколько сроков пропущено, если <see cref="Kind"/> — пропуск.</summary>
    public int MissedCount { get; init; }

    public string? Error { get; init; }

    /// <summary>Считается ли проверка наблюдением сети — только такие идут в доступность.</summary>
    public bool IsObservation => Kind == CheckKind.Measured && Level != VerdictLevel.Unknown;

    public bool IsDown => Kind == CheckKind.Measured && Level == VerdictLevel.Fail;
}

/// <summary>Фильтр для списка проверок.</summary>
public sealed record CheckQuery
{
    public Guid? MonitorId { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    public int Limit { get; init; } = 500;
}
