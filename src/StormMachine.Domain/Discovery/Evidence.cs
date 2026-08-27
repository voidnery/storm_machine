namespace StormMachine.Domain.Discovery;

/// <summary>Откуда получено свидетельство.</summary>
public enum EvidenceSource
{
    /// <summary>Таблица ARP операционной системы.</summary>
    ArpTable,

    /// <summary>Разрешение адреса запросом ARP (<c>SendARP</c>).</summary>
    ArpRequest,

    /// <summary>Ответ на ICMP echo.</summary>
    IcmpEcho,

    /// <summary>Установленное соединение TCP.</summary>
    TcpConnect,

    /// <summary>Обратная зона DNS.</summary>
    ReverseDns,

    /// <summary>Имя из NetBIOS.</summary>
    Netbios,

    /// <summary>Многоадресный DNS: имена и службы Apple, принтеры, телевизоры.</summary>
    Mdns,

    /// <summary>SSDP: маршрутизаторы, телевизоры, медиасерверы.</summary>
    Ssdp,

    /// <summary>Реестр IEEE: вендор по префиксу MAC.</summary>
    Oui,

    /// <summary>Узел встретился в трассировке.</summary>
    Traceroute,

    /// <summary>Правка оператора.</summary>
    Manual,
}

/// <summary>Что именно утверждает свидетельство.</summary>
public enum EvidenceKind
{
    /// <summary>Узел отвечает.</summary>
    Alive,

    MacAddress,

    HostName,

    Vendor,

    /// <summary>Роль узла: шлюз, принтер, точка доступа.</summary>
    Role,

    /// <summary>Открытый порт.</summary>
    OpenPort,
}

/// <summary>
/// Свидетельство о узле: кто, когда и с какой уверенностью это утверждает.
/// </summary>
/// <remarks>
/// Инвентарь не хранится как правда — он <b>вычисляется</b> из набора свидетельств.
/// Разница принципиальная: при таком устройстве повторное сканирование не может
/// затереть правку оператора, а противоречие между источниками разрешается правилом,
/// а не тем, кто записал последним.
/// <para>
/// Уверенность — не украшение. Имя из обратной зоны DNS и имя, введённое человеком,
/// утверждают одно и то же поле, но стоят разного; без явного веса победил бы тот,
/// кто пришёл позже.
/// </para>
/// </remarks>
public sealed record Evidence
{
    public required EvidenceSource Source { get; init; }

    public required EvidenceKind Kind { get; init; }

    public required string Value { get; init; }

    public required DateTimeOffset ObservedUtc { get; init; }

    /// <summary>
    /// Правка оператора: перекрывает любые наблюдения и переживает пересканирование.
    /// </summary>
    public bool IsPinned => Source == EvidenceSource.Manual;

    /// <summary>Вес источника при разрешении противоречий, 0…1.</summary>
    public double Confidence => Weight(Source);

    /// <summary>
    /// Вес источника.
    /// </summary>
    /// <remarks>
    /// Порядок не произвольный. MAC из ответа на собственный запрос ARP надёжнее MAC
    /// из системной таблицы: таблица могла устареть. Имя из mDNS точнее имени
    /// из обратной зоны DNS: первое устройство сообщает о себе само, второе взято
    /// из записи, которую мог оставить прежний владелец адреса.
    /// </remarks>
    public static double Weight(EvidenceSource source) => source switch
    {
        EvidenceSource.Manual => 1.00,
        EvidenceSource.ArpRequest => 0.95,
        EvidenceSource.Mdns => 0.90,
        EvidenceSource.Ssdp => 0.85,
        EvidenceSource.ArpTable => 0.80,
        EvidenceSource.Netbios => 0.75,
        EvidenceSource.Oui => 0.70,
        EvidenceSource.IcmpEcho => 0.70,
        EvidenceSource.TcpConnect => 0.70,
        EvidenceSource.ReverseDns => 0.60,
        EvidenceSource.Traceroute => 0.50,
        _ => 0.30,
    };

    public static Evidence Of(
        EvidenceSource source,
        EvidenceKind kind,
        string value,
        DateTimeOffset observedUtc) => new()
        {
            Source = source,
            Kind = kind,
            Value = value,
            ObservedUtc = observedUtc,
        };
}

/// <summary>
/// Разрешение противоречий между свидетельствами.
/// </summary>
/// <remarks>
/// Детерминированная функция от набора: одни и те же свидетельства всегда дают один
/// и тот же ответ, независимо от порядка их поступления. Без этого свойства повторное
/// сканирование меняло бы инвентарь произвольно, и различия между сканами перестали бы
/// что-либо значить.
/// </remarks>
public static class EvidenceMerge
{
    /// <summary>
    /// Выбирает значение для одного поля.
    /// </summary>
    /// <remarks>
    /// Порядок правил: правка оператора, затем вес источника, затем свежесть.
    /// Последним идёт сравнение самих значений — не ради смысла, а ради устойчивости:
    /// два одинаково весомых и одинаково свежих свидетельства обязаны давать
    /// один и тот же ответ при каждом пересчёте.
    /// </remarks>
    public static string? Resolve(IEnumerable<Evidence> evidence, EvidenceKind kind)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        Evidence? best = null;

        foreach (var item in evidence)
        {
            if (item.Kind != kind)
            {
                continue;
            }

            if (best is null || IsBetter(item, best))
            {
                best = item;
            }
        }

        return best?.Value;
    }

    private static bool IsBetter(Evidence candidate, Evidence current)
    {
        if (candidate.IsPinned != current.IsPinned)
        {
            return candidate.IsPinned;
        }

        if (Math.Abs(candidate.Confidence - current.Confidence) > double.Epsilon)
        {
            return candidate.Confidence > current.Confidence;
        }

        if (candidate.ObservedUtc != current.ObservedUtc)
        {
            return candidate.ObservedUtc > current.ObservedUtc;
        }

        return string.CompareOrdinal(candidate.Value, current.Value) < 0;
    }
}
