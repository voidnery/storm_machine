using StormMachine.Domain.Capabilities;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ возможностей продукта.
/// </summary>
/// <remarks>
/// Недоступное не прячется, а объясняется (UX-принцип 6). Спрятанная возможность
/// выглядит как отсутствующая: оператор либо ищет её в другом инструменте, либо
/// считает продукт неполным. Показанная с причиной — это задача, которую можно решить.
/// </remarks>
internal static class CapabilityRenderer
{
    public static void Write(CapabilityReport report, bool verbose)
    {
        ArgumentNullException.ThrowIfNull(report);

        Console.WriteLine(report.IsElevated
            ? "Продукт запущен с правами администратора."
            : "Продукт запущен без прав администратора — так и задумано: уровень 0 их не требует.");

        Console.WriteLine();

        foreach (var level in new[] { CapabilityLevel.Core, CapabilityLevel.Snmp, CapabilityLevel.Capture })
        {
            WriteLevel(report, level, verbose);
        }

        Console.WriteLine(
            $"Итого: работает {report.UsableCount}, "
            + $"требует действий {report.BlockedCount}, "
            + $"запланировано {report.PlannedCount}.");

        if (report.BlockedCount > 0 && !verbose)
        {
            Console.WriteLine("Подробности по каждой строке: storm capabilities --подробно");
        }
    }

    private static void WriteLevel(CapabilityReport report, CapabilityLevel level, bool verbose)
    {
        var items = report.OfLevel(level).ToList();

        if (items.Count == 0)
        {
            return;
        }

        Console.WriteLine($"{Mark(report.StateOf(level))} {Title(level),-46} {Describe(report.StateOf(level))}");

        foreach (var capability in items)
        {
            // Работающее показывается одной строкой, требующее внимания — с причиной.
            // Ровный список без различия читался бы как «всё одинаково», а это не так.
            if (capability.IsUsable && !verbose)
            {
                Console.WriteLine($"    {Mark(capability.State)} {capability.Title}");

                continue;
            }

            Console.WriteLine($"    {Mark(capability.State)} {capability.Title}");
            Console.WriteLine($"        {capability.About}");

            if (capability.Detail is { } detail)
            {
                Console.WriteLine($"        {detail}");
            }

            if (capability.HowToEnable is { } how)
            {
                Console.WriteLine($"        Нужно: {how}");
            }

            if (capability.Where is { } where)
            {
                Console.WriteLine($"        Где: {where}");
            }

            if (capability.Iteration is { } iteration)
            {
                Console.WriteLine($"        Появится в итерации {iteration}.");
            }
        }

        Console.WriteLine();
    }

    private static string Mark(CapabilityState state) => state switch
    {
        CapabilityState.Available => "+",
        CapabilityState.Limited => "~",
        CapabilityState.Planned => "·",
        _ => "!",
    };

    private static string Title(CapabilityLevel level) => level switch
    {
        CapabilityLevel.Core => "Уровень 0 — ядро. Ничего не требует",
        CapabilityLevel.Snmp => "Уровень 1 — SNMP. Нужны учётные данные оборудования",
        _ => "Уровень 2 — захват пакетов. Нужен Npcap",
    };

    private static string Describe(CapabilityState state) => state switch
    {
        CapabilityState.Available => "доступно",
        CapabilityState.Limited => "доступно частично",
        CapabilityState.NeedsElevation => "нужны права администратора",
        CapabilityState.NeedsCredentials => "нужны учётные данные",
        CapabilityState.NeedsDriver => "нужен драйвер",
        CapabilityState.NeedsData => "нужен файл базы",
        CapabilityState.NeedsAgent => "нужен агент на второй точке",
        _ => "ещё не сделано",
    };
}
