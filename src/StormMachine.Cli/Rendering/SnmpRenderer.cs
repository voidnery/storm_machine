using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Snmp;
using StormMachine.Domain.Snmp;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ того, что рассказало оборудование.
/// </summary>
/// <remarks>
/// Числа со счётчиков сами по себе не значат ничего: «17 ошибок» — это 17 ошибок
/// за всё время работы устройства, может быть, за три года. Поэтому здесь везде,
/// где показывается счётчик, рядом стоит либо промежуток, либо доля.
/// </remarks>
internal static class SnmpRenderer
{
    public static void WriteCredentials(IReadOnlyList<SnmpCredential> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (credentials.Count == 0)
        {
            Console.WriteLine("Учётных данных SNMP нет — опрашивать оборудование нечем.");
            Console.WriteLine();
            Console.WriteLine("Завести:");
            Console.WriteLine("  storm snmp creds add \"свитчи\" --версия v2c");
            Console.WriteLine("  storm snmp creds add \"ядро\" --версия v3 --пользователь storm --проверка sha256");
            Console.WriteLine();
            Console.WriteLine("Пароли спрашиваются отдельно и в историю оболочки не попадают.");

            return;
        }

        Console.WriteLine($"  {"набор",-20} {"как",-46} порядок");

        foreach (var credential in credentials)
        {
            Console.WriteLine(
                $"  {Cut(credential.Name, 20),-20} {Cut(credential.Describe(), 46),-46} "
                + credential.Order.ToString(CultureInfo.InvariantCulture));
        }

        Console.WriteLine();
        Console.WriteLine("Наборы пробуются по возрастанию порядка, пока какой-нибудь не подойдёт.");

        var warnings = credentials.SelectMany(c => c.Warnings().Select(w => (c.Name, Warning: w))).ToList();

        if (warnings.Count > 0)
        {
            Console.WriteLine();

            foreach (var (name, warning) in warnings.DistinctBy(w => w.Warning))
            {
                Console.WriteLine($"  ! {name}: {warning}");
            }
        }
    }

    public static void WriteSystem(string host, SnmpReach reach)
    {
        ArgumentNullException.ThrowIfNull(reach);

        var system = reach.System;

        Console.WriteLine();
        Console.WriteLine($"Узел      : {host}");
        Console.WriteLine($"Отвечает  : набором «{reach.Credential.Name}» ({reach.Credential.Describe()})");
        Console.WriteLine($"Имя       : {system.Name ?? "не задано"}");
        Console.WriteLine($"Модель    : {system.ShortDescription}");

        if (system.Location is { } where)
        {
            Console.WriteLine($"Где стоит : {where}");
        }

        if (system.Contact is { } contact)
        {
            Console.WriteLine($"Отвечает за: {contact}");
        }

        Console.WriteLine($"Работает  : {system.DescribeUpTime()}");

        // Недавняя перезагрузка объясняет половину жалоб сама, и сказать об этом
        // надо раньше, чем человек начнёт искать причину в другом месте.
        if (system.RestartedRecently)
        {
            Console.WriteLine();
            Console.WriteLine("  ! Устройство перезагрузилось меньше часа назад. Счётчики начались заново,");
            Console.WriteLine("    а обрывы сессий в это время объясняются этим, а не сетью.");
        }

        if (!reach.Credential.IsProtected)
        {
            Console.WriteLine();
            Console.WriteLine("  ! Опрос идёт без защиты: строка сообщества прошла по сети открытым текстом.");
        }
    }

    public static void WriteInterfaces(SnmpDevice device, IReadOnlyList<PortLoad> loads)
    {
        ArgumentNullException.ThrowIfNull(device);

        Console.WriteLine();
        Console.WriteLine($"{device.DisplayName} — {SnmpSystem.Describe(device.Role)}, "
                          + $"портов {device.Interfaces.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine();

        var withLoad = loads.Count > 0;

        Console.WriteLine(withLoad
            ? $"  {"#",-4} {"порт",-22} {"состояние",-24} {"скорость",-12} {"вход",-10} {"выход",-10} ошибки"
            : $"  {"#",-4} {"порт",-22} {"состояние",-24} {"скорость",-12} подпись");

        var byIndex = loads.ToDictionary(l => l.Interface.Index);

        foreach (var port in device.Interfaces)
        {
            var head =
                $"  {port.Index.ToString(CultureInfo.InvariantCulture),-4} {Cut(port.Name, 22),-22} "
                + $"{Cut(port.DescribeStatus(), 24),-24} {Cut(port.DescribeSpeed(), 12),-12} ";

            if (!withLoad)
            {
                Console.WriteLine(head + Cut(port.Alias ?? string.Empty, 30));

                continue;
            }

            if (!byIndex.TryGetValue(port.Index, out var measured) || measured.Load is not { } load)
            {
                var why = byIndex.TryGetValue(port.Index, out var refused)
                    ? InterfaceLoadCalculator.Describe(refused.Refusal)
                    : "нет данных";

                Console.WriteLine(head + Cut(why, 32));

                continue;
            }

            Console.WriteLine(
                head
                + $"{Rate(load.InBitsPerSecond, load.InPercent),-10} "
                + $"{Rate(load.OutBitsPerSecond, load.OutPercent),-10} "
                + Errors(load));
        }

        WriteInterfaceNotes(device, loads);
    }

    private static void WriteInterfaceNotes(SnmpDevice device, IReadOnlyList<PortLoad> loads)
    {
        Console.WriteLine();

        var dark = device.Interfaces.Where(i => i.IsPhysical && i.IsDark).ToList();

        if (dark.Count > 0)
        {
            Console.WriteLine(
                $"Включены, но без линка: {string.Join(", ", dark.Take(8).Select(i => i.Name))}"
                + (dark.Count > 8 ? " и ещё…" : string.Empty));
        }

        var noisy = loads
            .Where(l => l.Load?.InErrorsPerMillion > 100 || l.Load?.OutErrorsPerMillion > 100)
            .ToList();

        if (noisy.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Порты с ошибками — сотня на миллион кадров и выше:");

            foreach (var port in noisy)
            {
                Console.WriteLine(
                    $"  {port.Interface.DisplayName}: "
                    + $"вход {Per(port.Load!.InErrorsPerMillion)}, выход {Per(port.Load.OutErrorsPerMillion)}");
            }

            Console.WriteLine();
            Console.WriteLine("Такое обычно означает физику: патч-корд, разъём или несогласованный дуплекс.");
        }

        var implausible = loads.Where(l => l.Load?.IsImplausible == true).ToList();

        if (implausible.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"! На {implausible.Count.ToString(CultureInfo.InvariantCulture)} порт(ах) загрузка вышла выше 100%. "
                + "Так не бывает: врёт либо заявленная скорость порта, либо счётчики.");
        }
    }

    public static void WriteNeighbors(SnmpDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        Console.WriteLine();

        if (device.Neighbors.Count == 0)
        {
            Console.WriteLine($"{device.DisplayName}: соседей не объявлено.");
            Console.WriteLine();
            Console.WriteLine("Это не значит, что их нет: LLDP бывает выключен, а неуправляемый");
            Console.WriteLine("коммутатор не объявляет о себе никогда.");

            return;
        }

        Console.WriteLine($"{device.DisplayName} — соседей "
                          + $"{device.Neighbors.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine();
        Console.WriteLine($"  {"наш порт",-20} {"сосед",-28} {"его порт",-22} протокол");

        foreach (var neighbor in device.Neighbors.OrderBy(n => n.LocalIfIndex))
        {
            Console.WriteLine(
                $"  {Cut(neighbor.LocalPort ?? neighbor.LocalIfIndex.ToString(CultureInfo.InvariantCulture), 20),-20} "
                + $"{Cut(neighbor.DisplayName, 28),-28} {Cut(neighbor.RemotePort ?? "—", 22),-22} "
                + (neighbor.Protocol == NeighborProtocol.Lldp ? "LLDP" : "CDP"));
        }

        Console.WriteLine();
        Console.WriteLine("Между двумя устройствами с LLDP может стоять неуправляемый коммутатор:");
        Console.WriteLine("связь настоящая, но «прямая» она только логически.");
    }

    public static void WriteForwarding(SnmpDevice device, string? portFilter, string? macFilter)
    {
        ArgumentNullException.ThrowIfNull(device);

        Console.WriteLine();

        var entries = device.Forwarding.Where(f => f.IsLearned).ToList();

        if (entries.Count == 0)
        {
            Console.WriteLine($"{device.DisplayName}: таблица пересылки пуста или недоступна.");
            Console.WriteLine();
            Console.WriteLine("У маршрутизатора её и не бывает — он работает третьим уровнем.");

            return;
        }

        if (portFilter is not null)
        {
            entries = [.. entries.Where(f =>
                f.PortName?.Contains(portFilter, StringComparison.OrdinalIgnoreCase) == true)];
        }

        if (macFilter is not null)
        {
            var needle = macFilter.Replace(':', '-').Replace('.', '-');

            entries = [.. entries.Where(f => f.MacAddress.Contains(needle, StringComparison.OrdinalIgnoreCase))];
        }

        Console.WriteLine($"{device.DisplayName} — записей "
                          + $"{entries.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine();
        Console.WriteLine($"  {"MAC",-20} {"порт",-22} VLAN");

        foreach (var entry in entries.OrderBy(e => e.PortName, StringComparer.Ordinal).ThenBy(e => e.MacAddress))
        {
            Console.WriteLine(
                $"  {entry.MacAddress,-20} "
                + $"{Cut(entry.PortName ?? entry.IfIndex.ToString(CultureInfo.InvariantCulture), 22),-22} "
                + (entry.Vlan?.ToString(CultureInfo.InvariantCulture) ?? "—"));
        }

        // Порт с одним адресом — тот самый ответ, ради которого в коммутатор и лезут.
        var single = device.Ports().Where(p => p.SoleAddress is not null && p.Interface.IsPhysical).ToList();

        if (single.Count > 0 && portFilter is null && macFilter is null)
        {
            Console.WriteLine();
            Console.WriteLine("Порты с ровно одним адресом — скорее всего, конечные устройства:");

            foreach (var port in single.Take(12))
            {
                Console.WriteLine($"  {port.Interface.DisplayName}: {port.SoleAddress}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Записи живут по таймауту старения — обычно пять минут. Молчащее");
        Console.WriteLine("устройство из таблицы исчезает, хотя провод остаётся на месте.");
    }

    public static void WriteWalk(IReadOnlyList<SnmpVariable> variables, int limit)
    {
        ArgumentNullException.ThrowIfNull(variables);

        Console.WriteLine();

        foreach (var variable in variables)
        {
            Console.WriteLine($"  {variable.Oid} = {variable.Type}: {variable.Value}");
        }

        Console.WriteLine();
        Console.WriteLine($"Узлов: {variables.Count.ToString(CultureInfo.InvariantCulture)}");

        if (variables.Count >= limit)
        {
            Console.WriteLine($"Показ ограничен {limit.ToString(CultureInfo.InvariantCulture)} узлами. "
                              + "Больше — ключом --предел.");
        }
    }

    private static string Rate(double bitsPerSecond, double? percent)
    {
        var value = bitsPerSecond switch
        {
            < 1_000 => $"{bitsPerSecond.ToString("0", CultureInfo.InvariantCulture)}б",
            < 1_000_000 => $"{(bitsPerSecond / 1_000).ToString("0.#", CultureInfo.InvariantCulture)}К",
            < 1_000_000_000 => $"{(bitsPerSecond / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture)}М",
            _ => $"{(bitsPerSecond / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture)}Г",
        };

        return percent is { } share
            ? $"{value} {share.ToString("0", CultureInfo.InvariantCulture)}%"
            : value;
    }

    private static string Errors(InterfaceLoad load)
    {
        if (load.InErrors == 0 && load.OutErrors == 0 && load.InDiscards == 0 && load.OutDiscards == 0)
        {
            return "нет";
        }

        return $"вх {load.InErrors.ToString(CultureInfo.InvariantCulture)}"
               + $" / исх {load.OutErrors.ToString(CultureInfo.InvariantCulture)}"
               + (load.InDiscards + load.OutDiscards > 0
                   ? $", отброшено {(load.InDiscards + load.OutDiscards).ToString(CultureInfo.InvariantCulture)}"
                   : string.Empty);
    }

    private static string Per(double? perMillion) => perMillion is { } value
        ? $"{value.ToString("0", CultureInfo.InvariantCulture)} на млн"
        : "нет данных";

    private static string Cut(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";
}
