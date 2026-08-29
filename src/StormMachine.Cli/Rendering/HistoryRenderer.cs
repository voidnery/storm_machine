using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Cli.Commands;
using StormMachine.Domain.Text;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ истории наблюдений.
/// </summary>
/// <remarks>
/// История отвечает не на «сколько сейчас», а на «что менялось». Поэтому здесь
/// показывается не последняя точка, а поведение ряда: сколько наблюдений, между какими
/// значениями ходила загрузка и — главное — накопились ли ошибки. Растущий счётчик
/// ошибок находит умирающий патч-корд раньше, чем это заметит человек.
/// </remarks>
internal static class HistoryRenderer
{
    public static void WritePortLoad(IReadOnlyList<PortLoadPoint> points, int hours)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            Console.WriteLine($"За последние {HistoryCommands.Hours(hours)} наблюдений нет.");
            Console.WriteLine();
            Console.WriteLine("История накапливается при опросе: «storm snmp interfaces <устройство> --нагрузка».");
            Console.WriteLine("Один опрос — одна точка ряда; чтобы был график, опрашивать надо повторно,");
            Console.WriteLine("например монитором по расписанию.");

            return;
        }

        Console.WriteLine($"Наблюдений за {HistoryCommands.Hours(hours)}: {points.Count}");
        Console.WriteLine();

        var groups = points
            .GroupBy(p => (p.Device, p.IfIndex))
            .OrderBy(g => g.Key.Device, StringComparer.Ordinal)
            .ThenBy(g => g.Key.IfIndex);

        foreach (var group in groups)
        {
            var series = group.OrderBy(p => p.AtUtc).ToList();
            var last = series[^1];

            Console.WriteLine($"{last.Device}  порт {last.IfIndex}"
                              + (last.IfName is { Length: > 0 } name ? $"  {name}" : string.Empty));

            Console.WriteLine(
                $"  наблюдений {series.Count}, "
                + $"с {HistoryCommands.When(series[0].AtUtc)} по {HistoryCommands.When(last.AtUtc)}");

            WriteDirection("приём ", series.Select(p => (p.InBitsPerSecond, p.InPercent)).ToList());
            WriteDirection("отдача", series.Select(p => (p.OutBitsPerSecond, p.OutPercent)).ToList());

            WriteFaults(series);

            Console.WriteLine();
        }
    }

    private static void WriteDirection(string label, IReadOnlyList<(double Bps, double? Percent)> values)
    {
        var min = values.Min(v => v.Bps);
        var max = values.Max(v => v.Bps);
        var avg = values.Average(v => v.Bps);

        var percent = values[^1].Percent is { } share
            ? $"   последнее {share.ToString("0.0", CultureInfo.InvariantCulture)} % от скорости порта"
            : "   скорость порта неизвестна — проценты не считаются";

        Console.WriteLine($"  {label}  min {Rate(min)}  сред {Rate(avg)}  max {Rate(max)}{percent}");
    }

    /// <summary>
    /// Ошибки за период.
    /// </summary>
    /// <remarks>
    /// Показывается сумма и то, росла ли она. Единичная ошибка бывает у любого порта;
    /// вопрос в том, прибавляются ли они — и на этот вопрос отвечает только ряд,
    /// а не одно измерение.
    /// </remarks>
    private static void WriteFaults(List<PortLoadPoint> series)
    {
        var total = series.Sum(p => p.Faults);

        if (total == 0)
        {
            Console.WriteLine("  ошибок и отбросов за период не было");

            return;
        }

        var withFaults = series.Count(p => p.Faults > 0);

        Console.WriteLine(
            $"  ! ошибок и отбросов {total.ToString(CultureInfo.InvariantCulture)} "
            + $"в {Plural.With(withFaults, "наблюдении", "наблюдениях", "наблюдениях")} из {series.Count}");

        if (withFaults > 1)
        {
            Console.WriteLine("    Они прибавляются, а не случились однажды — это признак кабеля или порта,");
            Console.WriteLine("    а не разовой помехи.");
        }
    }

    private static string Rate(double bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => $"{(bitsPerSecond / 1_000_000_000).ToString("0.00", CultureInfo.InvariantCulture)} Гбит/с",
        >= 1_000_000 => $"{(bitsPerSecond / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture)} Мбит/с",
        >= 1_000 => $"{(bitsPerSecond / 1_000).ToString("0", CultureInfo.InvariantCulture)} кбит/с",
        _ => $"{bitsPerSecond.ToString("0", CultureInfo.InvariantCulture)} бит/с",
    };

    // ------------------------------------------------------------------ услышанное

    public static void WriteHeard(
        IReadOnlyList<HeardNeighbor> neighbors,
        IReadOnlyList<HeardDhcpServer> servers,
        IReadOnlyList<string> knownGateways,
        int days)
    {
        ArgumentNullException.ThrowIfNull(neighbors);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(knownGateways);

        if (neighbors.Count == 0 && servers.Count == 0)
        {
            Console.WriteLine($"За последние {HistoryCommands.Days(days)} в эфире ничего не слышали.");
            Console.WriteLine();
            Console.WriteLine("История накапливается при прослушивании: «storm capture listen --секунд 60».");
            Console.WriteLine("Оно требует установленного Npcap — продукт его не распространяет.");

            return;
        }

        if (neighbors.Count > 0)
        {
            Console.WriteLine($"Соседи, услышанные за {HistoryCommands.Days(days)}:");
            Console.WriteLine();

            foreach (var neighbor in neighbors.OrderBy(n => n.FirstSeenUtc))
            {
                Console.WriteLine(
                    $"  {neighbor.SystemName ?? neighbor.ChassisId,-28} {neighbor.PortId,-16} "
                    + $"{neighbor.Protocol,-5} через {neighbor.LocalInterface}");

                Console.WriteLine(
                    $"      впервые {HistoryCommands.When(neighbor.FirstSeenUtc)}, "
                    + $"последний раз {HistoryCommands.When(neighbor.LastSeenUtc)}");
            }

            Console.WriteLine();
        }

        if (servers.Count == 0)
        {
            return;
        }

        Console.WriteLine($"Серверы DHCP, услышанные за {HistoryCommands.Days(days)}:");
        Console.WriteLine();

        foreach (var server in servers.OrderBy(s => s.FirstSeenUtc))
        {
            var unknown = server.OfferedGateway.Length > 0
                          && !knownGateways.Contains(server.OfferedGateway, StringComparer.OrdinalIgnoreCase);

            Console.WriteLine(
                $"  {(unknown ? "!" : " ")} {server.ServerAddress,-16} шлюз {server.OfferedGateway,-16} "
                + $"услышан {server.Sightings.ToString(CultureInfo.InvariantCulture)} раз");

            Console.WriteLine(
                $"      впервые {HistoryCommands.When(server.FirstSeenUtc)}, "
                + $"последний раз {HistoryCommands.When(server.LastSeenUtc)}");

            if (server.OfferedDns.Count > 0)
            {
                Console.WriteLine($"      раздаёт DNS: {string.Join(", ", server.OfferedDns)}");
            }

            if (unknown)
            {
                Console.WriteLine("      Объявляет шлюз, которого мы не знаем.");
            }
        }

        Console.WriteLine();

        // Единственное утверждение, которое продукт делает сам, — про незнакомый шлюз.
        // Всё прочее остаётся фактами без вердикта: две законные пары DHCP в одном
        // домене встречаются не реже подставного сервера, и различить их может только
        // тот, кто знает свою сеть. История добавляет к фактам одно — дату появления,
        // и это уже повод посмотреть внимательно.
        var appeared = servers
            .Where(s => s.FirstSeenUtc > DateTimeOffset.UtcNow.AddDays(-days / 4.0))
            .ToList();

        if (appeared.Count > 0 && servers.Count > appeared.Count)
        {
            Console.WriteLine(
                $"Из них появились недавно: {string.Join(", ", appeared.Select(s => s.ServerAddress))}.");
            Console.WriteLine("Сервер, которого раньше не слышали, — повод посмотреть, откуда он взялся.");
        }
    }
}
