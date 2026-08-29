using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.Snmp;

/// <summary>
/// Строка таблицы пересылки: какой MAC виден на каком порту.
/// </summary>
/// <remarks>
/// BRIDGE-MIB отвечает на вопрос, ради которого чаще всего лезут в коммутатор:
/// <b>в какой порт воткнуто вот это устройство</b>. Инвентарь знает MAC, таблица
/// пересылки знает порт — вместе они дают то, чего не даёт ни одно измерение
/// с нашей машины.
/// <para>
/// Читать её надо с оглядкой. Записи живут по таймауту старения (обычно пять минут)
/// и исчезают, если устройство молчит. На порту, ведущем к другому коммутатору,
/// MAC-адресов будут десятки — это не значит, что все они воткнуты туда.
/// </para>
/// </remarks>
public sealed record ForwardingEntry
{
    public required string MacAddress { get; init; }

    /// <summary>Номер порта моста — <c>dot1dTpFdbPort</c>, не <c>ifIndex</c>.</summary>
    public required int BridgePort { get; init; }

    /// <summary><c>ifIndex</c>, полученный из <c>dot1dBasePortIfIndex</c>.</summary>
    public int IfIndex { get; init; }

    /// <summary>VLAN, если таблица читалась через Q-BRIDGE-MIB.</summary>
    public int? Vlan { get; init; }

    /// <summary>Имя порта, если удалось сопоставить.</summary>
    public string? PortName { get; init; }

    /// <summary>
    /// Запись выучена самим устройством, а не задана вручную.
    /// </summary>
    /// <remarks>
    /// <c>dot1dTpFdbStatus</c>: 3 — выучено, 4 — свой собственный адрес, 5 — задано
    /// администратором. Различие важно: собственный адрес моста на порту не означает,
    /// что там что-то воткнуто.
    /// </remarks>
    public bool IsLearned { get; init; } = true;
}

/// <summary>
/// Порт коммутатора вместе с тем, что на нём видно.
/// </summary>
/// <remarks>
/// Собирается из трёх источников: сам порт из <c>ifTable</c>, соседи из LLDP,
/// адреса из таблицы пересылки. Порознь они мало что значат; вместе отвечают
/// на вопрос «что к этому порту подключено» настолько точно, насколько это вообще
/// возможно без похода в серверную.
/// </remarks>
public sealed record SwitchPort
{
    public required SnmpInterface Interface { get; init; }

    public IReadOnlyList<LinkNeighbor> Neighbors { get; init; } = [];

    public IReadOnlyList<ForwardingEntry> Addresses { get; init; } = [];

    /// <summary>
    /// Порт ведёт к другому сетевому устройству, а не к конечному узлу.
    /// </summary>
    /// <remarks>
    /// Признак — либо объявленный сосед, либо много выученных адресов: за одним
    /// портом не бывает десяти компьютеров, если за ним не стоит ещё один коммутатор.
    /// Порог намеренно невысокий и намеренно приблизительный: это догадка, и она
    /// помечена как догадка.
    /// </remarks>
    public bool LooksLikeUplink => Neighbors.Count > 0 || Addresses.Count >= UplinkAddressHint;

    /// <summary>Сколько выученных адресов на порту наводит на мысль о втором коммутаторе.</summary>
    public const int UplinkAddressHint = 4;

    /// <summary>Единственный адрес на порту — то самое устройство, которое ищут.</summary>
    public string? SoleAddress => Addresses.Count == 1 ? Addresses[0].MacAddress : null;
}

/// <summary>
/// Всё, что удалось узнать об одном устройстве по SNMP.
/// </summary>
/// <remarks>
/// Отдельный тип, потому что топология строится не из ответов на отдельные запросы,
/// а из согласованного снимка: порты, соседи и таблица пересылки должны относиться
/// к одному моменту, иначе связи будут собраны из разных состояний сети.
/// </remarks>
public sealed record SnmpDevice
{
    public required string Address { get; init; }

    public required SnmpSystem System { get; init; }

    public required DateTimeOffset ObservedUtc { get; init; }

    public IReadOnlyList<SnmpInterface> Interfaces { get; init; } = [];

    public IReadOnlyList<LinkNeighbor> Neighbors { get; init; } = [];

    public IReadOnlyList<ForwardingEntry> Forwarding { get; init; } = [];

    /// <summary>Имя набора учётных данных, которым устройство удалось опросить.</summary>
    public string? Credential { get; init; }

    public SnmpDeviceRole Role => System.Role(Forwarding.Count > 0);

    public string DisplayName => System.Name ?? Address;

    /// <summary>Порты вместе с соседями и адресами на них.</summary>
    public IReadOnlyList<SwitchPort> Ports()
    {
        var byNeighbor = Neighbors.ToLookup(n => n.LocalIfIndex);
        var byAddress = Forwarding.Where(f => f.IsLearned).ToLookup(f => f.IfIndex);

        return
        [
            .. Interfaces.Select(i => new SwitchPort
            {
                Interface = i,
                Neighbors = [.. byNeighbor[i.Index]],
                Addresses = [.. byAddress[i.Index]],
            }),
        ];
    }
}
