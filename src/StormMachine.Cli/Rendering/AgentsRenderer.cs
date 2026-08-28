using StormMachine.Application.Abstractions;
using StormMachine.Domain.Agents;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ агентов.
/// </summary>
/// <remarks>
/// Отпечаток показывается всегда и в обоих концах: он единственное, что делает агента
/// этим агентом. Оператор, сверивший отпечаток на площадке с тем, что видит здесь,
/// знает, что говорит с той машиной. Спрятать его ради опрятности значило бы убрать
/// единственную проверку, которую человек может сделать сам.
/// </remarks>
internal static class AgentsRenderer
{
    public static void WriteList(IReadOnlyList<RemoteAgent> agents, string ownThumbprint)
    {
        ArgumentNullException.ThrowIfNull(agents);

        Console.WriteLine($"Наш отпечаток: {RemoteAgent.Group(ownThumbprint)}");
        Console.WriteLine();

        if (agents.Count == 0)
        {
            Console.WriteLine("Сопряжённых агентов нет.");
            Console.WriteLine();
            Console.WriteLine("Если на площадке можно открыть входящий порт:");
            Console.WriteLine("  там : storm-agent listen --сопряжение");
            Console.WriteLine("  тут : storm agents pair <адрес> --код <код>");
            Console.WriteLine();
            Console.WriteLine("Если прав там нет — звонить будет агент:");
            Console.WriteLine("  тут : storm agents pair --ждать");
            Console.WriteLine("  там : storm-agent connect <наш адрес> --код <код>");

            return;
        }

        Console.WriteLine($"  {"агент",-22} {"продукт",-22} {"связь",-28} последний раз");

        foreach (var agent in agents)
        {
            var seen = agent.LastSeenUtc is { } last
                ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : "не подключался";

            Console.WriteLine($"  {agent.DisplayName,-22} {agent.Product,-22} {agent.DescribeDirection(),-28} {seen}");
            Console.WriteLine($"  {string.Empty,-22} {agent.GroupedThumbprint}");

            if (agent.Capabilities.Count > 0)
            {
                Console.WriteLine($"  {string.Empty,-22} умеет: {string.Join(", ", agent.Capabilities)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Измерить скорость: storm throughput <агент>");
    }

    /// <summary>
    /// Кто объявился в эфире.
    /// </summary>
    /// <remarks>
    /// Пустой список не означает «агентов нет»: ответы приходят входящим трафиком,
    /// а он на Windows заблокирован по умолчанию. Сказать «никого» и промолчать
    /// о причине значило бы отправить оператора искать несуществующую поломку.
    /// </remarks>
    public static void WriteDiscovered(IReadOnlyList<DiscoveredAgent> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        Console.WriteLine();

        if (agents.Count == 0)
        {
            Console.WriteLine("В эфире никто не объявился.");
            Console.WriteLine();
            Console.WriteLine("Это не значит, что агентов нет. Объявления идут групповой рассылкой,");
            Console.WriteLine("а входящий трафик Windows блокирует по умолчанию — и на этой машине тоже.");
            Console.WriteLine("Обнаружение работает только в пределах одной подсети: агент на удалённой");
            Console.WriteLine("площадке так не найдётся никогда. Там адрес указывают руками.");

            return;
        }

        Console.WriteLine($"  {"машина",-22} {"адрес",-22} {"продукт",-22} состояние");

        foreach (var agent in agents)
        {
            var state = agent.IsAlreadyPaired ? "уже сопряжён" : "не сопряжён";

            Console.WriteLine($"  {agent.MachineName,-22} {agent.Address + ":" + agent.Port,-22} "
                              + $"{agent.Product ?? "—",-22} {state}");

            if (agent.ThumbprintPrefix is { Length: > 0 } prefix)
            {
                Console.WriteLine($"  {string.Empty,-22} начало отпечатка: {RemoteAgent.Group(prefix)}");
            }
        }

        Console.WriteLine();

        // Объявление — не доказательство. Подделать его может кто угодно в той же сети,
        // и единственное, что подтверждает личность, — отпечаток при сопряжении.
        Console.WriteLine("Объявлению верить нельзя: его может послать кто угодно в этой сети.");
        Console.WriteLine("Личность подтверждает только отпечаток при сопряжении.");
        Console.WriteLine("Сопрячься: storm agents pair <адрес> --код <код с машины агента>");
    }

    public static void WritePaired(RemoteAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        Console.WriteLine();
        Console.WriteLine($"Сопряжён: {agent.DisplayName} ({agent.Product})");
        Console.WriteLine($"Отпечаток: {agent.GroupedThumbprint}");
        Console.WriteLine($"Соединение: {agent.DescribeDirection()}");
        Console.WriteLine();

        // Сверка отпечатка — не формальность и не паранойя. Код сопряжения мог быть
        // подслушан или продиктован не тому, и единственный способ убедиться, что
        // сопряглись именно с той машиной, — сличить отпечаток на обоих концах.
        Console.WriteLine("Сверь этот отпечаток с тем, что показывает агент (storm-agent peers).");
        Console.WriteLine("Совпал — сопряжение состоялось с той машиной, с которой задумано.");
        Console.WriteLine();
        Console.WriteLine($"Дальше подтверждения не потребуется: storm throughput {agent.DisplayName}");

        if (agent.Direction == AgentDirection.AgentDials)
        {
            // Звонит агент — значит измерение начинается с ожидания его звонка,
            // и оператор должен знать это до того, как команда «зависнет».
            Console.WriteLine("Команда подождёт звонка агента; на его машине в этот момент нужно");
            Console.WriteLine("выполнить: storm-agent connect <адрес этой машины>");
        }
    }
}
