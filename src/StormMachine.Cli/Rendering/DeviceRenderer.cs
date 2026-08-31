using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;

namespace StormMachine.Cli.Rendering;

/// <summary>Показ результатов обнаружения и инвентаря.</summary>
internal static class DeviceRenderer
{
    private const int NameWidth = 26;
    private const int VendorWidth = 24;

    public static void WriteScanHeader(
        AddressRange range,
        NetworkAdapter? adapter,
        DiscoveryRequest request,
        IOuiCatalog oui)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(oui);

        Console.WriteLine($"Диапазон   : {range.Text}  ({range.First} … {range.Last})");
        Console.WriteLine($"Адресов    : {range.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Интерфейс  : {adapter?.Name ?? "неизвестен"}"
                          + (adapter?.IPv4Address is { } ip ? $", {ip}" : string.Empty));
        Console.WriteLine($"Темп       : {request.Parallelism.ToString(CultureInfo.InvariantCulture)} адресов "
                          + $"одновременно, таймаут {request.TimeoutMs.ToString(CultureInfo.InvariantCulture)} мс");
        Console.WriteLine($"Вендоры    : {oui.Count.ToString(CultureInfo.InvariantCulture)} записей реестра IEEE");

        if (!request.ProbeCommonPorts)
        {
            Console.WriteLine("Замечание  : проверка частых портов выключена — узлы с брандмауэром найдены не будут.");
        }
    }

    /// <summary>
    /// Строка хода сканирования, перерисовываемая на месте.
    /// </summary>
    /// <remarks>
    /// Сканирование /24 идёт секунды, но пустой экран в эти секунды выглядит как зависание.
    /// Строка перерисовывается возвратом каретки, а не переводом строки: две с половиной
    /// сотни строк прогресса не нужны никому.
    /// </remarks>
    public static Action<DiscoveryProgress> CreateProgressWriter()
    {
        // Перерисовка на месте работает только в живой консоли. При перенаправлении
        // в файл возврат каретки ничего не затирает, и вместо одной строки получается
        // сотня — прямо поверх результата, ради которого вывод и сохраняли.
        if (Console.IsOutputRedirected)
        {
            return _ => { };
        }

        var lastPercent = -1;

        return progress =>
        {
            var percent = (int)progress.Percent;

            if (percent == lastPercent)
            {
                return;
            }

            lastPercent = percent;

            Console.Write($"\r  опрошено {progress.Probed,5} из {progress.Total,-5} "
                          + $"({percent,3}%)   найдено: {progress.Found,3}   ");
        };
    }

    public static void WriteScan(DiscoveryScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        // Затираем строку прогресса: без этого её хвост останется под таблицей.
        if (!Console.IsOutputRedirected)
        {
            Console.Write('\r');
            Console.Write(new string(' ', 60));
            Console.Write('\r');
        }

        Console.WriteLine();
        Console.WriteLine($"--- {scan.Range} ---");

        if (scan.WasCancelled)
        {
            Console.WriteLine("Сканирование прервано. Ниже — то, что успели найти.");
        }

        if (scan.Devices.Count == 0)
        {
            Console.WriteLine("Устройств не найдено.");
            WriteScanSummary(scan);
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"адрес",-15} {"MAC",-17} {"имя",-NameWidth} {"вендор",-VendorWidth} что ответило");

        foreach (var device in scan.Devices)
        {
            WriteDevice(device);
        }

        WriteScanSummary(scan);
    }

    public static void WriteDevice(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var marker = device.Role == "шлюз" ? "→" : device.IsOnline ? " " : "·";

        Console.WriteLine($" {marker}{device.Address,-15} {device.MacAddress ?? "—",-17} "
                          + $"{Shorten(device.HostName, NameWidth),-NameWidth} "
                          + $"{Vendor(device),-VendorWidth} {Describe(device)}");

        WriteExtraAddresses(device);
    }

    /// <summary>
    /// Дописывает остальные адреса устройства.
    /// </summary>
    /// <remarks>
    /// Маршрутизаторы и гипервизоры занимают несколько адресов одним интерфейсом.
    /// Без этой строки инвентарь молча терял бы часть найденного: адресов 75,
    /// устройств 74, и разница ничем не объяснена.
    /// </remarks>
    private static void WriteExtraAddresses(Device device)
    {
        var extra = device.ExtraAddresses;

        if (extra.Count > 0)
        {
            Console.WriteLine($"       ещё адреса: {string.Join(", ", extra)}");
        }
    }

    /// <summary>
    /// Что показать в столбце принадлежности.
    /// </summary>
    /// <remarks>
    /// Не всегда вендор. У виртуального адреса VRRP реестр называет IANA — правда,
    /// которая говорит не о том; у локального адреса производителя нет вовсе.
    /// Решение принимает домен, показ только сокращает строку под ширину столбца.
    /// </remarks>
    private static string Vendor(Device device) => Shorten(device.VendorDisplay, VendorWidth);

    private static void WriteScanSummary(DiscoveryScan scan)
    {
        Console.WriteLine();
        Console.WriteLine($"Опрошено {scan.Probed} адресов, найдено {scan.Devices.Count}, "
                          + $"отвечают {scan.Responded}, MAC известен у {scan.WithMac}."
                          + (scan.Duration is { } duration
                              ? $" Заняло {duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} с."
                              : string.Empty));

        if (scan.Devices.Any(d => MacAddresses.DescribeVirtual(d.MacAddress) is not null))
        {
            Console.WriteLine();
            Console.WriteLine("  " + MacAddresses.VirtualExplanation);
        }

        var arpOnly = scan.Devices.Count(d => d.Evidence.Any(e =>
            e.Kind == EvidenceKind.Alive
            && e.Source is EvidenceSource.ArpTable or EvidenceSource.ArpRequest));

        if (arpOnly > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Из них {arpOnly} узлов найдены по ответу на ARP: они молчат на ICMP");
            Console.WriteLine("  и на проверяемые порты, но на втором уровне отвечают — иначе с ними");
            Console.WriteLine("  нельзя было бы разговаривать вовсе. Обычный ping-sweep их не находит.");
        }
    }

    /// <summary>Чем именно узел себя обнаружил.</summary>
    private static string Describe(Device device)
    {
        var sources = device.Evidence
            .Where(e => e.Kind == EvidenceKind.Alive)
            .Select(e => Name(e.Source))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var ports = device.OpenPorts.Count > 0
            ? " · порт " + string.Join(", ", device.OpenPorts)
            : string.Empty;

        // Тег категории (И-24): догадка классификатора приходит с вопросом,
        // и «сервер?» с «сервер» читаются по-разному — так и задумано.
        var role = device.RoleDisplay is { } tag && tag != "шлюз" ? $" · {tag}" : string.Empty;

        return (sources.Count == 0 ? "не отвечает" : string.Join(", ", sources) + ports) + role;
    }

    private static string Name(EvidenceSource source) => source switch
    {
        EvidenceSource.IcmpEcho => "ICMP",
        EvidenceSource.TcpConnect => "TCP",
        EvidenceSource.ArpTable => "ARP",
        EvidenceSource.ArpRequest => "ARP-запрос",
        EvidenceSource.Netbios => "NetBIOS",
        EvidenceSource.Mdns => "mDNS",
        EvidenceSource.Ssdp => "SSDP",
        EvidenceSource.Manual => "вручную",
        _ => source.ToString(),
    };

    public static void WriteInventory(IReadOnlyList<Device> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (devices.Count == 0)
        {
            Console.WriteLine("Инвентарь пуст. Запустите сканирование: storm discover");
            return;
        }

        Console.WriteLine($"  {"адрес",-15} {"MAC",-17} {"имя",-NameWidth} {"вендор",-VendorWidth} последний раз");

        foreach (var device in devices)
        {
            var marker = device.Role == "шлюз" ? "→" : device.IsOnline ? " " : "·";

            // Тег категории после времени: колонка с фиксированной шириной резала бы
            // «маршрутизатор» ровно на том, что несёт сведения.
            var role = device.RoleDisplay is { } tag && tag != "шлюз" ? $"  {tag}" : string.Empty;

            Console.WriteLine($" {marker}{device.Address,-15} {device.MacAddress ?? "—",-17} "
                              + $"{Shorten(device.HostName, NameWidth),-NameWidth} "
                              + $"{Vendor(device),-VendorWidth} "
                              + $"{device.LastSeenUtc.ToLocalTime():dd.MM HH:mm}{role}");

            WriteExtraAddresses(device);
        }

        var addresses = devices.Sum(d => Math.Max(1, d.Addresses.Count));

        Console.WriteLine();
        Console.WriteLine($"Всего {devices.Count} устройств на {addresses} адресах, "
                          + $"отвечали в последнем сканировании {devices.Count(d => d.IsOnline)}.");
        Console.WriteLine("  Точка перед адресом — устройство известно, но в последний раз не ответило.");

        if (addresses > devices.Count)
        {
            Console.WriteLine("  Устройств меньше, чем адресов: узел с несколькими адресами — это один узел,");
            Console.WriteLine("  и опознаётся он по MAC, а не по адресу.");
        }
    }

    public static void WriteDiff(ScanDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (diff.IsEmpty)
        {
            Console.WriteLine("Различий нет: сеть та же, что и была.");
            return;
        }

        if (diff.Appeared.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Появилось ({diff.Appeared.Count}):");

            foreach (var device in diff.Appeared)
            {
                Console.WriteLine($"    + {device.Address,-15} {device.DisplayName}");
            }
        }

        if (diff.Disappeared.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Пропало ({diff.Disappeared.Count}):");

            foreach (var device in diff.Disappeared)
            {
                Console.WriteLine($"    − {device.Address,-15} {device.DisplayName}");
            }
        }

        if (diff.Changed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Изменилось ({diff.Changed.Count}):");

            foreach (var (device, changes) in diff.Changed)
            {
                Console.WriteLine($"    ~ {device.Address,-15} {device.DisplayName}");

                foreach (var change in changes)
                {
                    Console.WriteLine($"        {change.Field}: {change.Before ?? "—"} → {change.After ?? "—"}");
                }
            }
        }
    }

    public static void WriteAudit(IReadOnlyList<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            Console.WriteLine("Активных действий не записано.");
            return;
        }

        Console.WriteLine($"  {"когда",-17} {"действие",-12} {"кто",-16} цель");

        foreach (var entry in entries)
        {
            Console.WriteLine($"  {entry.AtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss} {entry.Action,-12} "
                              + $"{Shorten(entry.Operator, 16),-16} {entry.Target}");

            if (entry.Details is { } details)
            {
                Console.WriteLine($"    {details}");
            }
        }
    }

    private static string Shorten(string? value, int width)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "—";
        }

        return value.Length <= width ? value : value[..(width - 1)] + "…";
    }
}
