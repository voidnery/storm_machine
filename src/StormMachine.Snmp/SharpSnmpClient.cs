using Lextm.SharpSnmpLib;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;
using SnmpException = StormMachine.Application.Abstractions.SnmpException;

namespace StormMachine.Snmp;

/// <summary>
/// Опрос оборудования поверх SharpSnmpLib.
/// </summary>
/// <remarks>
/// Библиотека изолирована здесь целиком: наружу выходят только доменные типы.
/// Правило то же, что у PDF и у раскладки графа, и по той же причине — заменить
/// реализацию протокола должно быть можно, не переписывая продукт.
/// <para>
/// Совместимость с обрезкой публикации проверена спайком-08 на всех путях, которыми
/// пользуется этот класс: разбор BER, массовое чтение таблиц, криптография третьей
/// версии.
/// </para>
/// </remarks>
public sealed class SharpSnmpClient : ISnmpClient
{
    /// <summary>Сколько строк таблицы читать самое большее.</summary>
    /// <remarks>
    /// Таблица пересылки коммутатора уровня доступа — сотни записей, ядра — десятки
    /// тысяч. Предел не столько защита от объёма, сколько от опроса, который идёт
    /// минутами и выглядит зависанием.
    /// </remarks>
    private const int TableLimit = 20_000;

    public async Task<SnmpSystem?> GetSystemAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default)
    {
        var session = await SnmpSession.OpenAsync(host, credential, cancellationToken).ConfigureAwait(false);

        // Читается одним запросом: семь отдельных стоили бы семи кругов по сети,
        // а на объекте через узкий канал это заметно.
        var answer = await session.GetAsync(
            [
                Oids.SysDescr,
                Oids.SysObjectId,
                Oids.SysUpTime,
                Oids.SysContact,
                Oids.SysName,
                Oids.SysLocation,
                Oids.SysServices,
            ],
            cancellationToken).ConfigureAwait(false);

        var byOid = answer.ToDictionary(v => v.Id.ToString(), v => v.Data, StringComparer.Ordinal);

        if (!byOid.TryGetValue(Oids.SysDescr, out var descr) || descr is Null or NoSuchObject or NoSuchInstance)
        {
            return null;
        }

        return new SnmpSystem
        {
            Description = SnmpValues.Text(descr),
            ObjectId = Optional(byOid, Oids.SysObjectId),
            UpTime = byOid.TryGetValue(Oids.SysUpTime, out var uptime)
                ? SnmpValues.Ticks(uptime)
                : TimeSpan.Zero,
            Contact = Optional(byOid, Oids.SysContact),
            Name = Optional(byOid, Oids.SysName),
            Location = Optional(byOid, Oids.SysLocation),
            Services = byOid.TryGetValue(Oids.SysServices, out var services)
                ? (int)SnmpValues.Number(services)
                : 0,
        };
    }

    public async Task<IReadOnlyList<SnmpInterface>> GetInterfacesAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default)
    {
        var session = await SnmpSession.OpenAsync(host, credential, cancellationToken).ConfigureAwait(false);

        var descriptions = await Column(session, Oids.IfDescr, cancellationToken).ConfigureAwait(false);
        var types = await Column(session, Oids.IfType, cancellationToken).ConfigureAwait(false);
        var speeds = await Column(session, Oids.IfSpeed, cancellationToken).ConfigureAwait(false);
        var admin = await Column(session, Oids.IfAdminStatus, cancellationToken).ConfigureAwait(false);
        var oper = await Column(session, Oids.IfOperStatus, cancellationToken).ConfigureAwait(false);
        var physical = await Column(session, Oids.IfPhysAddress, cancellationToken).ConfigureAwait(false);
        var mtu = await Column(session, Oids.IfMtu, cancellationToken).ConfigureAwait(false);

        // Расширенная таблица есть не у всех: первая версия протокола её не знает,
        // а простые устройства не реализуют. Её отсутствие — не отказ.
        var names = await Optional(() => Column(session, Oids.IfName, cancellationToken)).ConfigureAwait(false);
        var aliases = await Optional(() => Column(session, Oids.IfAlias, cancellationToken)).ConfigureAwait(false);
        var highSpeeds = await Optional(() => Column(session, Oids.IfHighSpeed, cancellationToken))
            .ConfigureAwait(false);

        var ports = new List<SnmpInterface>();

        foreach (var (index, description) in descriptions.OrderBy(p => p.Key))
        {
            var name = names.TryGetValue(index, out var shortName) && SnmpValues.Text(shortName).Length > 0
                ? SnmpValues.Text(shortName)
                : SnmpValues.Text(description);

            ports.Add(new SnmpInterface
            {
                Index = index,
                Name = name,
                Description = SnmpValues.Text(description),
                Alias = aliases.TryGetValue(index, out var alias) ? Blank(SnmpValues.Text(alias)) : null,
                Type = types.TryGetValue(index, out var type) ? (int)SnmpValues.Number(type) : 0,
                SpeedBitsPerSecond = Speed(index, speeds, highSpeeds),
                AdminStatus = Status(admin, index),
                OperStatus = Status(oper, index),
                PhysicalAddress = physical.TryGetValue(index, out var mac) ? SnmpValues.Mac(mac) : null,
                Mtu = mtu.TryGetValue(index, out var size) ? (int)SnmpValues.Number(size) : 0,
            });
        }

        return ports;
    }

    public async Task<IReadOnlyList<InterfaceCounters>> GetCountersAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default)
    {
        var session = await SnmpSession.OpenAsync(host, credential, cancellationToken).ConfigureAwait(false);

        var uptime = await session.GetAsync([Oids.SysUpTime], cancellationToken).ConfigureAwait(false);
        var at = DateTimeOffset.UtcNow;
        var since = uptime.Count > 0 ? SnmpValues.Ticks(uptime[0].Data) : TimeSpan.Zero;

        // 64-разрядные счётчики пробуются первыми и только они считаются надёжными:
        // 32-разрядный счётчик октетов на гигабитном порту переполняется за 34 секунды.
        var inOctets = await Optional(() => Column(session, Oids.IfHCInOctets, cancellationToken))
            .ConfigureAwait(false);

        var high = inOctets.Count > 0;

        var outOctets = high
            ? await Column(session, Oids.IfHCOutOctets, cancellationToken).ConfigureAwait(false)
            : [];

        var inPackets = high
            ? await Optional(() => Column(session, Oids.IfHCInUcastPkts, cancellationToken)).ConfigureAwait(false)
            : [];

        var outPackets = high
            ? await Optional(() => Column(session, Oids.IfHCOutUcastPkts, cancellationToken)).ConfigureAwait(false)
            : [];

        if (!high)
        {
            inOctets = await Column(session, Oids.IfInOctets, cancellationToken).ConfigureAwait(false);
            outOctets = await Column(session, Oids.IfOutOctets, cancellationToken).ConfigureAwait(false);
            inPackets = await Column(session, Oids.IfInUcastPkts, cancellationToken).ConfigureAwait(false);
            outPackets = await Column(session, Oids.IfOutUcastPkts, cancellationToken).ConfigureAwait(false);
        }

        var inErrors = await Column(session, Oids.IfInErrors, cancellationToken).ConfigureAwait(false);
        var outErrors = await Column(session, Oids.IfOutErrors, cancellationToken).ConfigureAwait(false);
        var inDiscards = await Column(session, Oids.IfInDiscards, cancellationToken).ConfigureAwait(false);
        var outDiscards = await Column(session, Oids.IfOutDiscards, cancellationToken).ConfigureAwait(false);

        return
        [
            .. inOctets.Keys.OrderBy(i => i).Select(index => new InterfaceCounters
            {
                Index = index,
                AtUtc = at,
                SysUpTime = since,
                AreHighCapacity = high,
                InOctets = Value(inOctets, index),
                OutOctets = Value(outOctets, index),
                InPackets = Value(inPackets, index),
                OutPackets = Value(outPackets, index),
                InErrors = Value(inErrors, index),
                OutErrors = Value(outErrors, index),
                InDiscards = Value(inDiscards, index),
                OutDiscards = Value(outDiscards, index),
            }),
        ];
    }

    public async Task<IReadOnlyList<LinkNeighbor>> GetNeighborsAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default)
    {
        var session = await SnmpSession.OpenAsync(host, credential, cancellationToken).ConfigureAwait(false);

        var found = new List<LinkNeighbor>();

        found.AddRange(await Lldp(session, cancellationToken).ConfigureAwait(false));

        // CDP читается только там, где LLDP молчит: у Cisco с включёнными обоими
        // соседи задвоились бы, а различить их снаружи нечем.
        if (found.Count == 0)
        {
            found.AddRange(await Cdp(session, cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

    public async Task<IReadOnlyList<ForwardingEntry>> GetForwardingAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default)
    {
        var session = await SnmpSession.OpenAsync(host, credential, cancellationToken).ConfigureAwait(false);

        var bridgeToIf = await Optional(() => Column(session, Oids.Dot1dBasePortIfIndex, cancellationToken))
            .ConfigureAwait(false);

        // Сначала ветка с VLAN (Q-BRIDGE), и только при пустой — старая. Раньше порядок
        // был обратным: старая ветка читалась первой, а вторая — лишь когда первая
        // ничего не дала. Но устройства с VLAN отдают старую ветку непустой — обычно
        // только для первой VLAN, — и номера VLAN терялись на любом реальном
        // коммутаторе: колонка была всегда пустой (найдено SNMP-стендом И-24).
        // Номер VLAN — не украшение: без него карта рисует соседями устройства
        // из разных широковещательных доменов, ровно то, что чинила И-23.
        var qEntries = await Optional(() => Walk(session, Oids.Dot1qTpFdbPort, cancellationToken))
            .ConfigureAwait(false);

        var qStatuses = await Optional(() => Walk(session, Oids.Dot1qTpFdbStatus, cancellationToken))
            .ConfigureAwait(false);

        var qStatus = qStatuses.ToDictionary(
            v => SnmpValues.Suffix(v.Id, Oids.Dot1qTpFdbStatus),
            v => (int)SnmpValues.Number(v.Data),
            StringComparer.Ordinal);

        var result = Read(qEntries, Oids.Dot1qTpFdbPort, qStatus, bridgeToIf, vlanAware: true);

        if (result.Count == 0)
        {
            var statuses = await Optional(() => Walk(session, Oids.Dot1dTpFdbStatus, cancellationToken))
                .ConfigureAwait(false);

            var status = statuses.ToDictionary(
                v => SnmpValues.Suffix(v.Id, Oids.Dot1dTpFdbStatus),
                v => (int)SnmpValues.Number(v.Data),
                StringComparer.Ordinal);

            var entries = await Optional(() => Walk(session, Oids.Dot1dTpFdbPort, cancellationToken))
                .ConfigureAwait(false);

            result = Read(entries, Oids.Dot1dTpFdbPort, status, bridgeToIf, vlanAware: false);
        }

        return result;
    }

    public async Task<IReadOnlyList<SnmpVariable>> WalkAsync(
        string host,
        SnmpCredential credential,
        string oid,
        int limit = 512,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oid);

        var session = await SnmpSession.OpenAsync(host, credential, cancellationToken).ConfigureAwait(false);
        var found = await session.WalkAsync(oid, limit, cancellationToken).ConfigureAwait(false);

        return
        [
            .. found.Select(v => new SnmpVariable(
                v.Id.ToString(),
                SnmpValues.TypeName(v.Data),
                SnmpValues.Text(v.Data))),
        ];
    }

    // ------------------------------------------------------------------ соседи

    private static async Task<IReadOnlyList<LinkNeighbor>> Lldp(
        SnmpSession session,
        CancellationToken cancellationToken)
    {
        var ports = await Optional(() => Walk(session, Oids.LldpRemPortId, cancellationToken))
            .ConfigureAwait(false);

        if (ports.Count == 0)
        {
            return [];
        }

        var names = await Index(session, Oids.LldpRemSysName, cancellationToken).ConfigureAwait(false);
        var chassis = await Index(session, Oids.LldpRemChassisId, cancellationToken).ConfigureAwait(false);
        var descriptions = await Index(session, Oids.LldpRemSysDesc, cancellationToken).ConfigureAwait(false);
        var portDescriptions = await Index(session, Oids.LldpRemPortDesc, cancellationToken).ConfigureAwait(false);

        var found = new List<LinkNeighbor>();

        foreach (var entry in ports)
        {
            var key = SnmpValues.Suffix(entry.Id, Oids.LldpRemPortId);
            var parts = SnmpValues.Parts(entry.Id, Oids.LldpRemPortId);

            // Индекс тройной: отметка времени, локальный порт, номер соседа.
            // Нужен средний — он и есть наш порт.
            if (parts.Length < 2)
            {
                continue;
            }

            found.Add(new LinkNeighbor
            {
                Protocol = NeighborProtocol.Lldp,
                LocalIfIndex = parts[1],
                RemotePort = Blank(SnmpValues.Text(entry.Data)),
                RemoteName = Pick(names, key),
                RemoteChassisId = Pick(chassis, key),
                RemoteDescription = Pick(descriptions, key),
                RemotePortDescription = Pick(portDescriptions, key),
            });
        }

        return found;
    }

    private static async Task<IReadOnlyList<LinkNeighbor>> Cdp(
        SnmpSession session,
        CancellationToken cancellationToken)
    {
        var devices = await Optional(() => Walk(session, Oids.CdpCacheDeviceId, cancellationToken))
            .ConfigureAwait(false);

        if (devices.Count == 0)
        {
            return [];
        }

        var ports = await Index(session, Oids.CdpCacheDevicePort, cancellationToken).ConfigureAwait(false);
        var platforms = await Index(session, Oids.CdpCachePlatform, cancellationToken).ConfigureAwait(false);

        var found = new List<LinkNeighbor>();

        foreach (var entry in devices)
        {
            var key = SnmpValues.Suffix(entry.Id, Oids.CdpCacheDeviceId);
            var parts = SnmpValues.Parts(entry.Id, Oids.CdpCacheDeviceId);

            // Здесь индекс двойной: ifIndex нашего порта и номер соседа на нём.
            if (parts.Length < 1)
            {
                continue;
            }

            found.Add(new LinkNeighbor
            {
                Protocol = NeighborProtocol.Cdp,
                LocalIfIndex = parts[0],
                RemoteName = Blank(SnmpValues.Text(entry.Data)),
                RemotePort = Pick(ports, key),
                RemoteDescription = Pick(platforms, key),
            });
        }

        return found;
    }

    // ------------------------------------------------------------------ таблица пересылки

    private static List<ForwardingEntry> Read(
        IReadOnlyList<Variable> entries,
        string column,
        Dictionary<string, int> statuses,
        Dictionary<int, ISnmpData> bridgeToIf,
        bool vlanAware)
    {
        var found = new List<ForwardingEntry>();

        foreach (var entry in entries)
        {
            var parts = SnmpValues.Parts(entry.Id, column);
            var mac = SnmpValues.MacFromTail(parts);

            if (mac is null)
            {
                continue;
            }

            var port = (int)SnmpValues.Number(entry.Data);

            if (port <= 0)
            {
                continue;
            }

            var key = SnmpValues.Suffix(entry.Id, column);

            // 3 — адрес выучен, 4 — собственный адрес моста, 5 — задан администратором.
            // Собственный адрес не означает, что в порт что-то воткнуто.
            var status = statuses.TryGetValue(key, out var code) ? code : 3;

            found.Add(new ForwardingEntry
            {
                MacAddress = mac,
                BridgePort = port,
                IfIndex = bridgeToIf.TryGetValue(port, out var ifIndex) ? (int)SnmpValues.Number(ifIndex) : port,
                Vlan = vlanAware && parts.Length >= 7 ? parts[0] : null,
                IsLearned = status == 3,
            });
        }

        return found;
    }

    // ------------------------------------------------------------------ вспомогательное

    private static async Task<IReadOnlyList<Variable>> Walk(
        SnmpSession session,
        string column,
        CancellationToken cancellationToken) =>
        await session.WalkAsync(column, TableLimit, cancellationToken).ConfigureAwait(false);

    /// <summary>Столбец таблицы, разложенный по номеру строки.</summary>
    private static async Task<Dictionary<int, ISnmpData>> Column(
        SnmpSession session,
        string column,
        CancellationToken cancellationToken)
    {
        var found = await Walk(session, column, cancellationToken).ConfigureAwait(false);
        var byIndex = new Dictionary<int, ISnmpData>();

        foreach (var variable in found)
        {
            if (SnmpValues.Index(variable.Id, column) is { } index)
            {
                byIndex[index] = variable.Data;
            }
        }

        return byIndex;
    }

    /// <summary>Столбец с составным индексом: ключом остаётся хвост целиком.</summary>
    private static async Task<Dictionary<string, ISnmpData>> Index(
        SnmpSession session,
        string column,
        CancellationToken cancellationToken)
    {
        var found = await Optional(() => Walk(session, column, cancellationToken)).ConfigureAwait(false);

        return found.ToDictionary(v => SnmpValues.Suffix(v.Id, column), v => v.Data, StringComparer.Ordinal);
    }

    /// <summary>Ветка, которой у устройства может не быть.</summary>
    private static async Task<IReadOnlyList<Variable>> Optional(Func<Task<IReadOnlyList<Variable>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (SnmpException ex) when (ex.Reason is SnmpFailure.NoSuchObject)
        {
            return [];
        }
    }

    private static async Task<Dictionary<int, ISnmpData>> Optional(Func<Task<Dictionary<int, ISnmpData>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (SnmpException ex) when (ex.Reason is SnmpFailure.NoSuchObject)
        {
            return [];
        }
    }

    private static long Value(Dictionary<int, ISnmpData> column, int index) =>
        column.TryGetValue(index, out var data) ? SnmpValues.Number(data) : 0;

    private static string? Pick(Dictionary<string, ISnmpData> column, string key) =>
        column.TryGetValue(key, out var data) ? Blank(SnmpValues.Text(data)) : null;

    private static string? Optional(Dictionary<string, ISnmpData> byOid, string oid) =>
        byOid.TryGetValue(oid, out var data) ? Blank(SnmpValues.Text(data)) : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static InterfaceStatus Status(Dictionary<int, ISnmpData> column, int index) =>
        column.TryGetValue(index, out var data)
            ? (InterfaceStatus)(int)SnmpValues.Number(data)
            : InterfaceStatus.Unknown;

    /// <summary>
    /// Скорость порта.
    /// </summary>
    /// <remarks>
    /// Предпочитается <c>ifHighSpeed</c> в мегабитах: 32-разрядный <c>ifSpeed</c>
    /// упирается в 4.29 Гбит/с, и десятигигабитный порт по нему неотличим
    /// от четырёхгигабитного.
    /// </remarks>
    private static long Speed(
        int index,
        Dictionary<int, ISnmpData> speeds,
        Dictionary<int, ISnmpData> highSpeeds)
    {
        if (highSpeeds.TryGetValue(index, out var high))
        {
            var megabits = SnmpValues.Number(high);

            if (megabits > 0)
            {
                return megabits * 1_000_000L;
            }
        }

        return speeds.TryGetValue(index, out var speed) ? SnmpValues.Number(speed) : 0;
    }
}
