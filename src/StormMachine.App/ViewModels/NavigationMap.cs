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
    public const string Path = "/internet/path";
    public const string Discovery = "/local/discovery";
    public const string Devices = "/local/devices";
    public const string Topology = "/local/topology";
    public const string Runs = "/runs";
    public const string Presets = "/presets";
    public const string Probes = "/internet/probes";
    public const string Inspect = "/internet/inspect";
    public const string Monitors = "/monitors";
    public const string Schedule = "/schedule";
    public const string Alerts = "/alerts";
    public const string Reports = "/reports";
    public const string Settings = "/settings";

    public static IReadOnlyList<NavigationSection> Sections { get; } =
    [
        new(Dashboard, "Дашборд", "Состояние сети, активные мониторы, алерты, быстрый запуск.", null),
        new(Latency, "Задержка", "Непрерывный ping с живым графиком, джиттером и PDV.", null),
        new(Path, "Анализ пути", "Traceroute и непрерывный MTR: потери и задержка по каждому хопу.", null),
        new(Discovery, "Обнаружение", "Сканирование подсети: какие узлы в ней есть.", null),
        new(Devices, "Устройства", "Инвентарь: адреса, MAC, вендор, имена и что изменилось.", null),
        new(Topology, "Карта сети", "Граф сети с видимым различием подтверждённого и выведенного.", null),
        new(Presets, "Библиотека", "Пресеты: именованные тесты, которые можно повторить и передать.", null),
        new(Runs, "Журнал", "История прогонов с разбором до сырых измерений.", null),
        new(Probes, "Внешние пробы", "Сценарии из цепочки шагов с разбивкой по фазам и порогами.", null),
        new(Inspect, "Инспекторы", "DNS, TLS и HTTP: разбор ответов и таймингов. Взгляд снаружи.", null),
        new("/local/tests", "Локальные тесты", "Скорость между точками, bufferbloat, DHCP, DNS, NTP.", "И-13"),
        new("/internet/speed", "Скорость и качество", "Speedtest и bufferbloat.", "И-13"),
        new(Monitors, "Мониторы", "Постоянные проверки, доступность, SLA.", null),
        new(Schedule, "Расписание", "Периодические запуски и окна обслуживания.", null),
        new(Reports, "Отчёты", "Технический, сводка, акт тестирования и SLA. С методиками и условиями.", null),
        new(Alerts, "Алерты", "Лента событий и состояние каналов доставки.", null),
        new(Settings, "Настройки", "Что доступно на этой машине, профили окружения, хранилище.", null),
    ];
}
