using System.Globalization;
using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.Capture;

/// <summary>
/// Адаптер, на котором можно слушать.
/// </summary>
/// <remarks>
/// Не то же самое, что сетевой адаптер операционной системы: драйвер захвата видит
/// свой список и называет устройства по-своему. Сопоставление с адаптером системы
/// делается по MAC — по имени они не совпадают ни на одной версии Windows.
/// </remarks>
public sealed record CaptureAdapter
{
    /// <summary>Имя в терминах драйвера: <c>\Device\NPF_{GUID}</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Человеческое описание от драйвера.</summary>
    public required string Description { get; init; }

    /// <summary>MAC — единственная надёжная связь с адаптером системы.</summary>
    public string? MacAddress { get; init; }

    /// <summary>Имя адаптера в системе, если сопоставить удалось.</summary>
    public string? SystemName { get; init; }

    public bool IsLoopback { get; init; }

    public string DisplayName => SystemName ?? Description;
}

/// <summary>Каким сообщением DHCP себя обнаружил сервер.</summary>
public enum DhcpMessage
{
    Offer,

    Ack,

    Nak,

    /// <summary>Прочее — важно только то, что отвечал сервер, а не клиент.</summary>
    Other,
}

/// <summary>
/// Ответ DHCP, услышанный в эфире.
/// </summary>
/// <remarks>
/// Слушаются именно <b>ответы</b>: запросы шлёт клиент, и по ним о серверах ничего
/// не узнать. Один ответ — это один факт «в этом широковещательном домене есть DHCP
/// вот с таким адресом», и больше ничего: посторонний он или законный, из одного
/// пакета не следует.
/// </remarks>
public sealed record DhcpSighting
{
    /// <summary>Адрес сервера — из опции 54, а не из адреса отправителя.</summary>
    /// <remarks>
    /// Отправитель может быть агентом ретрансляции: он пересылает чужие ответы
    /// и на роль сервера не претендует. Опция 54 называет того, кто выдаёт адреса.
    /// </remarks>
    public required string ServerAddress { get; init; }

    /// <summary>MAC отправителя кадра — им отличают подмену адреса от настоящего сервера.</summary>
    public string? ServerMac { get; init; }

    public required DhcpMessage Message { get; init; }

    /// <summary>Какой адрес предложен клиенту.</summary>
    public string? OfferedAddress { get; init; }

    /// <summary>Какой шлюз объявлен клиенту — опция 3.</summary>
    public string? OfferedGateway { get; init; }

    /// <summary>Какие DNS объявлены клиенту — опция 6.</summary>
    public IReadOnlyList<string> OfferedDns { get; init; } = [];

    public required DateTimeOffset ObservedUtc { get; init; }
}

/// <summary>
/// Что известно про DHCP в этом сегменте после прослушивания.
/// </summary>
/// <remarks>
/// Продукт <b>не объявляет сервер посторонним</b> — он показывает, сколько их услышано
/// и что каждый раздаёт. Причина простая: два сервера в одном домене бывают и законно
/// (отказоустойчивая пара), а один-единственный бывает и подставным. Отличить их может
/// человек, знающий свою сеть, — и ему для этого нужны факты, а не вердикт инструмента.
/// <para>
/// Единственное, что продукт берётся утверждать сам, — <b>несовпадение с тем, что
/// известно о сети</b>: сервер вне своей подсети или объявляющий чужой шлюз. Это
/// проверяемое утверждение, а не догадка о намерениях.
/// </para>
/// </remarks>
public sealed record DhcpFinding
{
    public required IReadOnlyList<DhcpSighting> Sightings { get; init; }

    /// <summary>Сколько разных серверов ответило.</summary>
    public int ServerCount => Sightings
        .Select(s => s.ServerAddress)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public IReadOnlyList<string> Servers =>
    [
        .. Sightings
            .Select(s => s.ServerAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Больше одного сервера — повод разобраться, но ещё не приговор.</summary>
    public bool NeedsAttention => ServerCount > 1;

    /// <summary>
    /// Серверы, объявляющие шлюз, которого мы не знаем.
    /// </summary>
    /// <param name="knownGateways">Шлюзы, назначенные нашим адаптерам.</param>
    /// <remarks>
    /// Самый убедительный признак постороннего DHCP из всех, что можно получить
    /// пассивно: чужой сервер обычно выдаёт себя же в качестве шлюза, а значит
    /// уводит через себя весь трафик клиента.
    /// </remarks>
    public IReadOnlyList<DhcpSighting> Mismatched(IReadOnlyCollection<string> knownGateways)
    {
        ArgumentNullException.ThrowIfNull(knownGateways);

        if (knownGateways.Count == 0)
        {
            return [];
        }

        return
        [
            .. Sightings.Where(s => s.OfferedGateway is { } gateway
                                    && !knownGateways.Contains(gateway, StringComparer.OrdinalIgnoreCase)),
        ];
    }

    public static DhcpFinding Empty { get; } = new() { Sightings = [] };
}

/// <summary>
/// Итог прослушивания.
/// </summary>
/// <remarks>
/// Захват <b>только слушает</b>. Ни одного кадра продукт в сеть не отправляет:
/// ни поддельного ARP, ни запроса DHCP, ни попытки заставить оборудование объявиться.
/// Это та же граница, что у SNMP без записи — инструмент диагностики не должен уметь
/// менять то, что измеряет (docs/01-analysis.md §1.4).
/// <para>
/// Пустой результат — обычное дело, а не отказ. LLDP объявляется раз в 30 секунд,
/// CDP — раз в 60, ответы DHCP звучат только когда кто-то просит адрес. Тридцать
/// секунд тишины ничего не опровергают, и продукт обязан это сказать вслух.
/// </para>
/// </remarks>
public sealed record CaptureResult
{
    public required CaptureAdapter Adapter { get; init; }

    public required TimeSpan Duration { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Сколько кадров прошло через фильтр.</summary>
    public int FramesSeen { get; init; }

    public IReadOnlyList<LinkNeighbor> Neighbors { get; init; } = [];

    public DhcpFinding Dhcp { get; init; } = DhcpFinding.Empty;

    /// <summary>Кадры, которые не удалось разобрать. Считаются, но не хранятся.</summary>
    public int Unparsed { get; init; }

    public bool IsEmpty => Neighbors.Count == 0 && Dhcp.Sightings.Count == 0;

    /// <summary>
    /// Оговорка о длительности.
    /// </summary>
    /// <remarks>
    /// Печатается всегда, когда ничего не услышано. Молчание за короткое окно — это
    /// «не услышали», а не «нет»: соседи объявляются раз в полминуты, а DHCP отвечает
    /// только на чей-то запрос.
    /// </remarks>
    public string? Caveat => IsEmpty
        ? $"За {Describe(Duration)} не услышано ничего. Это не значит, что соседей и DHCP нет: "
          + "LLDP объявляется раз в 30 секунд, CDP — раз в 60, а ответы DHCP звучат, "
          + "только когда кто-то просит адрес."
        : Duration < TimeSpan.FromSeconds(60)
            ? $"Слушали {Describe(Duration)}. Устройство, объявляющееся раз в минуту, "
              + "могло не попасть в это окно."
            : null;

    public static string Describe(TimeSpan span) => span.TotalSeconds < 60
        ? $"{span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} с"
        : $"{span.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} мин";
}
