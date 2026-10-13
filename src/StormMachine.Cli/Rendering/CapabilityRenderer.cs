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

            if (Command(capability.Remedy) is { } command)
            {
                Console.WriteLine($"        {command}");
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

    /// <summary>
    /// Команда, которой это делается в консоли.
    /// </summary>
    /// <remarks>
    /// Синтаксис команд живёт здесь, а не в ядре. Ядро называет действие, каждый
    /// клиент выполняет его по-своему: консоль показывает команду, окно ставит
    /// кнопку. Пока команда лежала в ядре, окно показывало «storm snmp creds add»
    /// человеку, который открыл окно именно затем, чтобы не ходить в консоль.
    /// </remarks>
    private static string? Command(CapabilityRemedy remedy) => remedy switch
    {
        CapabilityRemedy.SnmpCredentials =>
            "Команда: storm snmp creds add \"свитчи\" --версия v2c — пароль спросится отдельно "
            + "и в историю оболочки не попадёт.",
        CapabilityRemedy.PairAgent => "Команда: storm agents pair — код сопряжения живёт десять минут.",
        CapabilityRemedy.Elevate => "Запустить консоль от имени администратора и повторить команду.",
        CapabilityRemedy.InstallCaptureDriver => null,
        _ => null,
    };

    private static string Mark(CapabilityState state) => CapabilityWording.Mark(state);

    private static string Title(CapabilityLevel level) => level switch
    {
        CapabilityLevel.Core => "Уровень 0 — ядро. Ничего не требует",
        CapabilityLevel.Snmp => "Уровень 1 — SNMP. Нужны учётные данные оборудования",
        // Без приписки про права: состояние уровня печатается следом и говорит
        // то же самое. «Нужны права администратора нужны права администратора» —
        // ровно то, что увидел протокол приёмки.
        _ => "Уровень 2 — захват кадров",
    };

    private static string Describe(CapabilityState state) => CapabilityWording.State(state);
}
