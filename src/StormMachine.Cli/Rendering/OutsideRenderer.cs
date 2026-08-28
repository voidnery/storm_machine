using System.Globalization;
using StormMachine.Domain.Outside;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ того, как сеть видна снаружи.
/// </summary>
/// <remarks>
/// Порядок строк — от факта к выводу: сначала адрес, каким его увидели чужие серверы,
/// потом что из этого следует. Обратный порядок заставил бы читать вывод, не зная,
/// на чём он основан.
/// </remarks>
internal static class OutsideRenderer
{
    public static void Write(OutsideView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        Console.WriteLine("--- Как сеть видна снаружи ---");
        Console.WriteLine();

        Console.WriteLine($"  {"Локальный адрес",-24} {Describe(view.LocalAddress, view.LocalPort)}");
        Console.WriteLine($"  {"Внешний адрес",-24} {Describe(view.ExternalAddress, view.ExternalPort)}");

        if (view.HostName is { Length: > 0 } hostName)
        {
            Console.WriteLine($"  {"Обратная запись",-24} {hostName}");
        }

        if (view.AsNumber is { } asn)
        {
            Console.WriteLine($"  {"Автономная система",-24} AS{asn}"
                              + (view.AsOrganization is { Length: > 0 } org ? $" — {org}" : string.Empty));
        }

        if (view.Country is { Length: > 0 } country)
        {
            Console.WriteLine($"  {"Страна",-24} {country}");
        }

        Console.WriteLine();
        WriteNat(view);

        if (view.Ipv6 is { } ipv6)
        {
            Console.WriteLine();
            WriteIpv6(ipv6);
        }

        WriteNotes(view);
    }

    private static void WriteNat(OutsideView view)
    {
        Console.WriteLine($"  {"Трансляция адресов",-24} {view.DescribeMapping()}");

        // Ответы серверов показываются целиком: вывод о поведении NAT сделан из них,
        // и без них его нечем проверить. Одно слово «симметричный» пришлось бы принять
        // на веру — а инструмент диагностики на веру ничего просить не должен.
        foreach (var mapping in view.Mappings)
        {
            var seen = mapping.Answered
                ? $"видит нас как {mapping.Address}:{mapping.Port.ToString(CultureInfo.InvariantCulture)}"
                : $"не ответил ({mapping.Failure ?? "причина неизвестна"})";

            Console.WriteLine($"      {mapping.Server,-28} {seen}");
        }

        Console.WriteLine($"      {OutsideView.FilteringNotTested}");
    }

    private static void WriteIpv6(Ipv6Readiness ipv6)
    {
        Console.WriteLine($"  {"Готовность к IPv6",-24} {ipv6.Describe()}");

        Console.WriteLine($"      {"глобальный адрес",-28} "
                          + (ipv6.GlobalAddress ?? "нет"));
        Console.WriteLine($"      {"запись AAAA у цели",-28} "
                          + (ipv6.AaaaAddress ?? "нет"));
        Console.WriteLine($"      {"соединение по IPv6",-28} "
                          + (ipv6.Reachable ? "устанавливается" : ipv6.Failure ?? "не устанавливается"));
    }

    private static void WriteNotes(OutsideView view)
    {
        if (view.Notes.Count == 0 && view.Attribution is null)
        {
            return;
        }

        Console.WriteLine();

        foreach (var note in view.Notes)
        {
            Console.WriteLine($"  · {note}");
        }

        if (view.Attribution is { Length: > 0 } attribution)
        {
            Console.WriteLine($"  Источник данных о принадлежности: {attribution}");
        }
    }

    private static string Describe(string? address, int port) =>
        address is null
            ? "не определён"
            : port > 0
                ? $"{address}:{port.ToString(CultureInfo.InvariantCulture)}"
                : address;
}
