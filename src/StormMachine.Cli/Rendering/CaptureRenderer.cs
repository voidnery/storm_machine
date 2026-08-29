using System.Globalization;
using StormMachine.Application.Capture;
using StormMachine.Domain.Capture;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ того, что услышано в эфире.
/// </summary>
/// <remarks>
/// Главное здесь — <b>оговорки печатаются всегда</b>. Пустой улов за тридцать секунд
/// значит «не услышали», а не «нет»: соседи объявляются раз в полминуты, а ответы DHCP
/// звучат только на чей-то запрос. Показать пустой список без этой оговорки значит
/// подсунуть отсутствие данных под видом результата.
/// </remarks>
internal static class CaptureRenderer
{
    public static void WriteAvailability(CaptureService capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        Console.WriteLine(capture.Explain());

        if (capture.IsAvailable)
        {
            Console.WriteLine();
            Console.WriteLine($"Драйвер: {capture.DriverDescription ?? "версия не определена"}");
            Console.WriteLine();
            Console.WriteLine("Продукт слушает без неразборчивого режима: только то, что адресовано");
            Console.WriteLine("нам или разослано всем. Чужая переписка по сегменту не собирается.");
            Console.WriteLine();
            Console.WriteLine("Послушать: storm capture listen --секунд 60");

            return;
        }

        Console.WriteLine();
        Console.WriteLine("Всё остальное в продукте работает и без захвата — это уровень 2,");
        Console.WriteLine("и он необязателен. Уровни целиком: storm capabilities.");
    }

    public static void WriteAdapters(IReadOnlyList<CaptureAdapter> adapters, CaptureAdapter? primary)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        if (adapters.Count == 0)
        {
            Console.WriteLine("Драйвер захвата не показывает ни одного адаптера.");

            return;
        }

        Console.WriteLine($"  {"адаптер",-34} {"MAC",-20} что это");

        foreach (var adapter in adapters)
        {
            var mark = primary is not null && string.Equals(adapter.Id, primary.Id, StringComparison.Ordinal)
                ? "*"
                : " ";

            Console.WriteLine(
                $"{mark} {Cut(adapter.DisplayName, 34),-34} {adapter.MacAddress ?? "—",-20} "
                + Cut(adapter.IsLoopback ? "петля" : adapter.Description, 40));
        }

        Console.WriteLine();
        Console.WriteLine("Звёздочкой отмечен адаптер, через который идёт маршрут по умолчанию:");
        Console.WriteLine("он смотрит в ту сеть, про которую обычно и спрашивают.");
    }

    public static void WriteResult(CaptureResult result, IReadOnlyList<string> knownGateways)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(knownGateways);

        Console.WriteLine($"Слушали {CaptureResult.Describe(result.Duration)} на {result.Adapter.DisplayName}. "
                          + $"Кадров через фильтр: {result.FramesSeen.ToString(CultureInfo.InvariantCulture)}.");

        WriteNeighbors(result);
        WriteDhcp(result.Dhcp, knownGateways);

        if (result.Caveat is { } caveat)
        {
            Console.WriteLine();
            Console.WriteLine(caveat);
        }
    }

    private static void WriteNeighbors(CaptureResult result)
    {
        Console.WriteLine();

        if (result.Neighbors.Count == 0)
        {
            Console.WriteLine("Соседей не услышано.");

            return;
        }

        Console.WriteLine($"Соседи — {result.Neighbors.Count.ToString(CultureInfo.InvariantCulture)}:");
        Console.WriteLine();
        Console.WriteLine($"  {"сосед",-28} {"его порт",-24} {"протокол",-10} описание");

        foreach (var neighbor in result.Neighbors)
        {
            Console.WriteLine(
                $"  {Cut(neighbor.DisplayName, 28),-28} {Cut(neighbor.RemotePort ?? "—", 24),-24} "
                + $"{neighbor.ProtocolName,-10} {Cut(neighbor.RemoteDescription ?? string.Empty, 36)}");
        }

        Console.WriteLine();
        Console.WriteLine("Это соседи нашего адаптера, а не всего коммутатора: услышано то,");
        Console.WriteLine("что долетело до нас. Полную картину по портам даёт storm snmp neighbors.");
    }

    private static void WriteDhcp(DhcpFinding dhcp, IReadOnlyList<string> knownGateways)
    {
        Console.WriteLine();

        if (dhcp.Sightings.Count == 0)
        {
            Console.WriteLine("Ответов DHCP не слышно. Они звучат только когда кто-то просит адрес.");

            return;
        }

        Console.WriteLine($"Серверы DHCP — {dhcp.ServerCount.ToString(CultureInfo.InvariantCulture)}:");
        Console.WriteLine();
        Console.WriteLine($"  {"сервер",-18} {"MAC",-20} {"объявляет шлюз",-18} ответов");

        foreach (var server in dhcp.Servers)
        {
            var byServer = dhcp.Sightings
                .Where(s => string.Equals(s.ServerAddress, server, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var gateways = byServer
                .Select(s => s.OfferedGateway)
                .Where(g => g is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine(
                $"  {server,-18} {byServer[0].ServerMac ?? "—",-20} "
                + $"{(gateways.Count > 0 ? string.Join(", ", gateways) : "—"),-18} "
                + byServer.Count.ToString(CultureInfo.InvariantCulture));
        }

        var mismatched = dhcp.Mismatched(knownGateways);

        if (mismatched.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("! Сервер объявляет шлюз, которого мы не знаем:");

            foreach (var sighting in mismatched.DistinctBy(s => s.ServerAddress))
            {
                Console.WriteLine($"    {sighting.ServerAddress} → шлюз {sighting.OfferedGateway}");
            }

            Console.WriteLine();
            Console.WriteLine("Посторонний сервер обычно выдаёт себя же шлюзом и уводит через себя");
            Console.WriteLine("весь трафик клиента. Это проверяемое утверждение, а не догадка.");
        }
        else if (dhcp.NeedsAttention)
        {
            Console.WriteLine();
            Console.WriteLine("Серверов больше одного. Само по себе это не приговор: отказоустойчивая");
            Console.WriteLine("пара — обычное дело. Шлюз каждый объявляет тот же, что известен системе.");
        }

        Console.WriteLine();
        Console.WriteLine("Продукт не берётся называть сервер посторонним: два законных сервера");
        Console.WriteLine("в одном домене бывают, а один подставной — тоже. Различает тот, кто знает сеть.");
    }

    private static string Cut(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";
}
