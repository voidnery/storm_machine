using System.Globalization;

namespace StormMachine.Domain.Discovery;

/// <summary>Каким протоколом объявлен сосед.</summary>
public enum NeighborProtocol
{
    /// <summary>IEEE 802.1AB — стандарт, понимают почти все.</summary>
    Lldp,

    /// <summary>Cisco Discovery Protocol — там, где LLDP выключен.</summary>
    Cdp,
}

/// <summary>
/// Откуда мы узнали о соседе.
/// </summary>
/// <remarks>
/// Различие не косметическое. Опрос по SNMP спрашивает <b>у коммутатора</b>, кого он
/// видит: ответ охватывает все его порты сразу, включая те, куда наша машина
/// не подключена. Захват слышит кадры <b>своим адаптером</b>: он видит только то,
/// что долетает до нас, зато не требует ни учётных данных, ни того, чтобы у устройства
/// вообще был SNMP.
/// <para>
/// Отсюда правило: источники не заменяют, а дополняют друг друга, и путать их
/// на карте нельзя.
/// </para>
/// </remarks>
public enum NeighborSource
{
    /// <summary>Опрошено по SNMP: <c>lldpRemTable</c> или кэш CDP устройства.</summary>
    Snmp,

    /// <summary>Услышано своим адаптером: кадр LLDP или CDP пришёл к нам.</summary>
    Capture,
}

/// <summary>
/// Сосед, о котором устройство объявило само.
/// </summary>
/// <remarks>
/// Самое ценное свидетельство о топологии второго уровня, какое вообще бывает:
/// устройство называет и свой порт, и порт соседа. Ни ARP, ни трассировка такого
/// не дают — они говорят «в одном сегменте», а не «этим проводом в этот порт».
/// <para>
/// Оговорка, которую нельзя опускать: между двумя устройствами с LLDP может стоять
/// неуправляемый коммутатор, и тогда они всё равно видят друг друга соседями.
/// Связь настоящая, но «прямая» она только логически.
/// </para>
/// <para>
/// Тип переехал из пространства SNMP в И-18: с появлением захвата у одного и того же
/// факта стало два источника, и называть его «соседом по SNMP» стало неправдой.
/// </para>
/// </remarks>
public sealed record LinkNeighbor
{
    public required NeighborProtocol Protocol { get; init; }

    public NeighborSource Source { get; init; } = NeighborSource.Snmp;

    /// <summary>Порт, на котором виден сосед. <c>ifIndex</c> локального устройства.</summary>
    public required int LocalIfIndex { get; init; }

    /// <summary>Имя нашего порта, если удалось сопоставить.</summary>
    public string? LocalPort { get; init; }

    /// <summary>Как сосед себя называет: <c>lldpRemSysName</c> или <c>cdpCacheDeviceId</c>.</summary>
    public string? RemoteName { get; init; }

    /// <summary>Идентификатор шасси соседа — обычно его базовый MAC.</summary>
    public string? RemoteChassisId { get; init; }

    /// <summary>Порт соседа, которым он к нам подключён.</summary>
    public string? RemotePort { get; init; }

    /// <summary>Описание порта соседа — часто содержит подпись администратора.</summary>
    public string? RemotePortDescription { get; init; }

    /// <summary>Модель и прошивка соседа, если объявлены.</summary>
    public string? RemoteDescription { get; init; }

    /// <summary>Адрес управления соседа, если он его объявил.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>Когда услышан или опрошен.</summary>
    public DateTimeOffset ObservedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Как назвать соседа в отчёте и на карте.</summary>
    public string DisplayName => RemoteName ?? RemoteAddress ?? RemoteChassisId ?? "сосед без имени";

    public string ProtocolName => Protocol == NeighborProtocol.Lldp ? "LLDP" : "CDP";

    /// <summary>Строка «почему» для связи на карте.</summary>
    public string Because =>
        $"{ProtocolName}{(Source == NeighborSource.Capture ? " (услышан своим адаптером)" : string.Empty)}: "
        + $"порт {LocalPort ?? LocalIfIndex.ToString(CultureInfo.InvariantCulture)}"
        + (RemotePort is null ? string.Empty : $" ↔ {RemotePort}");
}
