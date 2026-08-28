using System.Globalization;

namespace StormMachine.Domain.Snmp;

/// <summary>Чем устройство работает в сети.</summary>
public enum SnmpDeviceRole
{
    Unknown,

    /// <summary>Конечный узел: сервер, рабочая станция, принтер.</summary>
    Host,

    /// <summary>Коммутатор: пересылает кадры второго уровня.</summary>
    Switch,

    /// <summary>Маршрутизатор: пересылает пакеты третьего уровня.</summary>
    Router,

    /// <summary>И то, и другое — обычное состояние современного оборудования.</summary>
    LayerThreeSwitch,

    /// <summary>Точка доступа.</summary>
    AccessPoint,
}

/// <summary>
/// Системная группа устройства — RFC 1213 §6.1.
/// </summary>
/// <remarks>
/// Первое, что спрашивают у оборудования, и первое, что стоит показать человеку:
/// имя, модель, где стоит и сколько работает без перезагрузки. Последнее особенно —
/// коммутатор, поднявшийся двадцать минут назад, объясняет половину жалоб сам.
/// </remarks>
public sealed record SnmpSystem
{
    /// <summary><c>sysDescr</c>: производитель, модель, версия прошивки одной строкой.</summary>
    public required string Description { get; init; }

    /// <summary><c>sysName</c>: как устройство названо в своей конфигурации.</summary>
    public string? Name { get; init; }

    /// <summary><c>sysObjectID</c>: ветка производителя в дереве OID.</summary>
    public string? ObjectId { get; init; }

    /// <summary><c>sysContact</c>: кто отвечает за устройство.</summary>
    public string? Contact { get; init; }

    /// <summary><c>sysLocation</c>: где оно стоит.</summary>
    public string? Location { get; init; }

    /// <summary><c>sysUpTime</c>: сколько работает с последней перезагрузки.</summary>
    public TimeSpan UpTime { get; init; }

    /// <summary>
    /// <c>sysServices</c>: сумма 2^(L−1) по уровням, на которых устройство работает.
    /// </summary>
    /// <remarks>
    /// Второй уровень даёт 2, третий — 4, прикладной — 64. Значение заявляет
    /// производитель, и заявляет как придётся: L3-коммутаторы встречаются
    /// и с 2, и с 6, и с 78. Отсюда правило — считать это <b>подсказкой</b>,
    /// а решать по наличию таблицы пересылки.
    /// </remarks>
    public int Services { get; init; }

    public bool ClaimsBridging => (Services & 0x02) != 0;

    public bool ClaimsRouting => (Services & 0x04) != 0;

    /// <summary>Часть <c>sysDescr</c> до первой запятой — обычно производитель и модель.</summary>
    public string ShortDescription
    {
        get
        {
            var cut = Description.IndexOfAny([',', ';', '\n']);

            return cut > 0 ? Description[..cut].Trim() : Description.Trim();
        }
    }

    /// <summary>
    /// Кем считать устройство.
    /// </summary>
    /// <param name="hasForwardingTable">Устройство отдало таблицу пересылки BRIDGE-MIB.</param>
    /// <remarks>
    /// Таблица пересылки весит больше заявленных услуг: её наличие означает, что
    /// устройство действительно коммутирует кадры, а <c>sysServices</c> означает лишь,
    /// что кто-то так написал в прошивке.
    /// </remarks>
    public SnmpDeviceRole Role(bool hasForwardingTable)
    {
        if (hasForwardingTable)
        {
            return ClaimsRouting ? SnmpDeviceRole.LayerThreeSwitch : SnmpDeviceRole.Switch;
        }

        if (ClaimsRouting)
        {
            return SnmpDeviceRole.Router;
        }

        return ClaimsBridging ? SnmpDeviceRole.Switch : SnmpDeviceRole.Host;
    }

    public static string Describe(SnmpDeviceRole role) => role switch
    {
        SnmpDeviceRole.Host => "конечный узел",
        SnmpDeviceRole.Switch => "коммутатор",
        SnmpDeviceRole.Router => "маршрутизатор",
        SnmpDeviceRole.LayerThreeSwitch => "коммутатор третьего уровня",
        SnmpDeviceRole.AccessPoint => "точка доступа",
        _ => "роль не определена",
    };

    /// <summary>Время работы человеческим языком.</summary>
    public string DescribeUpTime() => UpTime switch
    {
        { TotalDays: >= 1 } => $"{((int)UpTime.TotalDays).ToString(CultureInfo.InvariantCulture)} сут "
                               + $"{UpTime.Hours.ToString(CultureInfo.InvariantCulture)} ч",
        { TotalHours: >= 1 } => $"{((int)UpTime.TotalHours).ToString(CultureInfo.InvariantCulture)} ч "
                                + $"{UpTime.Minutes.ToString(CultureInfo.InvariantCulture)} мин",
        _ => $"{((int)UpTime.TotalMinutes).ToString(CultureInfo.InvariantCulture)} мин",
    };

    /// <summary>
    /// Недавняя перезагрузка.
    /// </summary>
    /// <remarks>
    /// Час — не магическое число, а срок, на котором вопрос «а оно не перезагружалось?»
    /// перестаёт быть первым. Показывается рядом со временем работы, потому что
    /// объясняет обрыв сессий и пустые счётчики раньше, чем их начнут искать.
    /// </remarks>
    public bool RestartedRecently => UpTime < TimeSpan.FromHours(1);
}
