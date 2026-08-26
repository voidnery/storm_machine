namespace StormMachine.App.ViewModels;

/// <summary>
/// Карта разделов приложения.
/// </summary>
/// <remarks>
/// Повторяет дерево из docs/01-analysis.md §9.1. Пути разделов — это же будущие адреса
/// в web-варианте, поэтому они заданы здесь один раз и не выдумываются заново каждым экраном.
/// </remarks>
internal static class NavigationMap
{
    /// <summary>Разделы, у которых уже есть настоящая страница.</summary>
    public const string Dashboard = "/";
    public const string Latency = "/local/tests/latency";
    public const string Runs = "/runs";

    public static IReadOnlyList<NavigationSection> Sections { get; } =
    [
        new(Dashboard, "Дашборд", "Состояние сети, активные мониторы, алерты, быстрый запуск.", null),
        new(Latency, "Задержка", "Непрерывный ping с живым графиком, джиттером и PDV.", null),
        new(Runs, "Журнал", "История прогонов с разбором до сырых измерений.", null),
        new("/local/topology", "Карта сети", "Граф топологии с редактором и отметками достоверности связей.", "И-9"),
        new("/local/devices", "Устройства", "Инвентарь: адреса, MAC, вендор, имена, история.", "И-8"),
        new("/local/discovery", "Обнаружение", "Сканирование подсети, история сканов, различия между ними.", "И-8"),
        new("/local/tests", "Локальные тесты", "Скорость между точками, bufferbloat, DHCP, DNS, NTP.", "И-13"),
        new("/internet/probes", "Внешние пробы", "Сценарии probe до узлов в интернете.", "И-11"),
        new("/internet/path", "Анализ пути", "Traceroute и непрерывный MTR с потерями по хопам.", "И-7"),
        new("/internet/speed", "Скорость и качество", "Speedtest, bufferbloat, IPv6, тип NAT.", "И-13"),
        new("/internet/inspect", "Инспекторы", "DNS, TLS и HTTP: разбор ответов и таймингов.", "И-11"),
        new("/monitors", "Мониторы", "Постоянные проверки, доступность, SLA.", "И-14"),
        new("/presets", "Библиотека", "Пресеты и наборы тестов.", "И-5"),
        new("/schedule", "Расписание", "Периодические запуски и окна обслуживания.", "И-14"),
        new("/reports", "Отчёты", "Формирование PDF с методиками и условиями измерения.", "И-6"),
        new("/alerts", "Алерты", "Правила и лента событий.", "И-14"),
        new("/settings", "Настройки", "Профили сети, агенты, учётные данные, хранилище.", "И-16"),
    ];
}
