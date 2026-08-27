using System.Globalization;

namespace StormMachine.Domain.Discovery;

/// <summary>
/// Устройство в сети, каким его видит инвентарь.
/// </summary>
/// <remarks>
/// Все поля, кроме адреса и времён наблюдения, — <b>вычисленные</b>: они выводятся
/// из свидетельств правилом слияния, а не записываются напрямую. Отсюда следует
/// главное свойство инвентаря: правка оператора переживает пересканирование,
/// потому что она тоже свидетельство, только с наивысшим весом.
/// </remarks>
public sealed record Device
{
    /// <summary>
    /// Устойчивое тождество устройства.
    /// </summary>
    /// <remarks>
    /// MAC, если он известен, и только иначе — адрес. Различие не косметическое:
    /// в сети с DHCP адрес меняется, и опознание по адресу показало бы одно устройство
    /// как два, а различия между сканами — как исчезновение и появление там,
    /// где ничего не происходило.
    /// </remarks>
    public string Identity => MacAddress ?? Address;

    /// <summary>Основной адрес — наименьший из известных.</summary>
    public required string Address { get; init; }

    /// <summary>
    /// Все адреса устройства.
    /// </summary>
    /// <remarks>
    /// Маршрутизаторы, гипервизоры и хосты с несколькими подсетями занимают несколько
    /// адресов одним интерфейсом. Показывать только один значило бы сообщать, что часть
    /// найденного куда-то делась: сканирование находит 75 адресов, а инвентарь
    /// перечисляет 74 устройства — и разница ничем не объяснена.
    /// </remarks>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Адреса сверх основного — их и показывают рядом с ним.</summary>
    public IReadOnlyList<string> ExtraAddresses =>
        [.. Addresses.Where(a => !string.Equals(a, Address, StringComparison.Ordinal))];

    public string? MacAddress { get; init; }

    public string? Vendor { get; init; }

    public string? HostName { get; init; }

    public string? Role { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Отвечал ли узел в последнем сканировании.</summary>
    public required bool IsOnline { get; init; }

    /// <summary>Открытые порты, замеченные при обнаружении.</summary>
    public IReadOnlyList<int> OpenPorts { get; init; } = [];

    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    /// <summary>Как назвать устройство человеку.</summary>
    public string DisplayName => HostName ?? Vendor ?? Address;

    /// <summary>
    /// MAC назначен локально, а не выдан производителю.
    /// </summary>
    /// <remarks>
    /// Второй бит первого октета означает локальное назначение. Так выглядят
    /// случайные адреса телефонов с приватным Wi-Fi, виртуальные адаптеры Hyper-V
    /// и Docker, мосты и агрегированные интерфейсы. Вендора у такого адреса нет
    /// и быть не может — и пустая ячейка в этом случае должна читаться
    /// как «неоткуда взять», а не как «не нашли».
    /// </remarks>
    public bool HasLocalMacAddress => MacAddresses.IsLocallyAdministered(MacAddress);

    /// <summary>
    /// Что показать в столбце принадлежности.
    /// </summary>
    /// <remarks>
    /// Не всегда вендор. У виртуального адреса VRRP реестр называет IANA — правда,
    /// которая говорит не о том; у локального адреса производителя нет вовсе.
    /// </remarks>
    public string VendorDisplay => MacAddresses.Describe(MacAddress, Vendor);

    /// <summary>
    /// Собирает устройство из свидетельств.
    /// </summary>
    public static Device FromEvidence(
        string address,
        IReadOnlyList<Evidence> evidence,
        DateTimeOffset firstSeenUtc,
        DateTimeOffset lastSeenUtc,
        bool isOnline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(evidence);

        var ports = new List<int>();

        foreach (var item in evidence)
        {
            if (item.Kind == EvidenceKind.OpenPort
                && int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        ports.Sort();

        return new Device
        {
            Address = address,
            Addresses = [address],
            MacAddress = EvidenceMerge.Resolve(evidence, EvidenceKind.MacAddress),
            Vendor = EvidenceMerge.Resolve(evidence, EvidenceKind.Vendor),
            HostName = EvidenceMerge.Resolve(evidence, EvidenceKind.HostName),
            Role = EvidenceMerge.Resolve(evidence, EvidenceKind.Role),
            FirstSeenUtc = firstSeenUtc,
            LastSeenUtc = lastSeenUtc,
            IsOnline = isOnline,
            OpenPorts = ports,
            Evidence = evidence,
        };
    }
}

/// <summary>Что изменилось в одном устройстве между сканированиями.</summary>
public sealed record DeviceChange(string Field, string? Before, string? After);

/// <summary>Различия между двумя сканированиями.</summary>
/// <remarks>
/// Ради этого инвентарь и ведётся: «что появилось в сети со вчера» — вопрос,
/// на который список устройств сам по себе не отвечает.
/// </remarks>
public sealed record ScanDiff
{
    public required IReadOnlyList<Device> Appeared { get; init; }

    public required IReadOnlyList<Device> Disappeared { get; init; }

    public required IReadOnlyList<(Device Device, IReadOnlyList<DeviceChange> Changes)> Changed { get; init; }

    public bool IsEmpty => Appeared.Count == 0 && Disappeared.Count == 0 && Changed.Count == 0;

    public static ScanDiff Between(IReadOnlyList<Device> before, IReadOnlyList<Device> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var previous = Group(before);
        var current = Group(after);

        var appeared = new List<Device>();
        var changed = new List<(Device, IReadOnlyList<DeviceChange>)>();

        foreach (var (identity, entry) in current)
        {
            if (!previous.TryGetValue(identity, out var old))
            {
                appeared.Add(entry.Device);
                continue;
            }

            var changes = Compare(old, entry);

            if (changes.Count > 0)
            {
                changed.Add((entry.Device, changes));
            }
        }

        var disappeared = previous
            .Where(pair => !current.ContainsKey(pair.Key))
            .Select(pair => pair.Value.Device)
            .ToList();

        return new ScanDiff
        {
            Appeared = appeared,
            Disappeared = disappeared,
            Changed = changed,
        };
    }

    /// <summary>Устройство и все его адреса в одном сканировании.</summary>
    private sealed record Entry(Device Device, string Addresses);

    /// <summary>
    /// Сводит записи сканирования по тождеству устройства.
    /// </summary>
    /// <remarks>
    /// Одно устройство может занимать несколько адресов — так устроены маршрутизаторы,
    /// гипервизоры и хосты с несколькими подсетями. Без сведения такой узел попадал бы
    /// в различия при каждом сканировании: сравнение брало бы то один его адрес,
    /// то другой и объявляло это сменой адреса.
    /// </remarks>
    private static Dictionary<string, Entry> Group(IReadOnlyList<Device> devices)
    {
        var buckets = new Dictionary<string, List<Device>>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (!buckets.TryGetValue(device.Identity, out var bucket))
            {
                bucket = [];
                buckets[device.Identity] = bucket;
            }

            bucket.Add(device);
        }

        var result = new Dictionary<string, Entry>(buckets.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (identity, bucket) in buckets)
        {
            // Представителем берётся отвечающая запись с наименьшим адресом: выбор
            // должен быть одинаковым при каждом пересчёте, иначе различия окажутся
            // следствием порядка чтения, а не изменений в сети.
            var representative = bucket
                .OrderByDescending(d => d.IsOnline)
                .ThenBy(d => d.Address, StringComparer.Ordinal)
                .First();

            var addresses = bucket
                .SelectMany(d => d.Addresses.Count > 0 ? d.Addresses : [d.Address])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();

            result[identity] = new Entry(
                representative with { Addresses = addresses },
                string.Join(", ", addresses));
        }

        return result;
    }

    private static List<DeviceChange> Compare(Entry before, Entry after)
    {
        var changes = new List<DeviceChange>();

        Add(changes, "адрес", before.Addresses, after.Addresses);
        Add(changes, "MAC", before.Device.MacAddress, after.Device.MacAddress);
        Add(changes, "имя", before.Device.HostName, after.Device.HostName);
        Add(changes, "вендор", before.Device.Vendor, after.Device.Vendor);

        if (before.Device.IsOnline != after.Device.IsOnline)
        {
            changes.Add(new DeviceChange(
                "доступность",
                before.Device.IsOnline ? "отвечал" : "не отвечал",
                after.Device.IsOnline ? "отвечает" : "не отвечает"));
        }

        return changes;
    }

    /// <summary>
    /// Записывает изменение поля.
    /// </summary>
    /// <remarks>
    /// Пропажа значения изменением не считается. Имя, которое было и не стало, —
    /// почти всегда не переименование, а неответивший резолвер: обратный DNS
    /// отвечает не каждый раз. Показывать это как изменение значит наполнить
    /// список различий шумом и утопить в нём настоящие события.
    /// <para>
    /// Устройство, которое действительно исчезло, попадёт в свой раздел целиком —
    /// для этого сравнение полей не нужно.
    /// </para>
    /// </remarks>
    private static void Add(List<DeviceChange> changes, string field, string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal) || after is null)
        {
            return;
        }

        changes.Add(new DeviceChange(field, before, after));
    }
}
