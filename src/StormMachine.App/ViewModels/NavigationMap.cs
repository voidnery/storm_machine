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
    public const string LocalTests = "/local/tests";
    public const string Speed = "/internet/speed";
    public const string Monitors = "/monitors";
    public const string Schedule = "/schedule";
    public const string Alerts = "/alerts";
    public const string Reports = "/reports";
    public const string Settings = "/settings";

    /// <summary>
    /// Временный раздел разработки (И-24).
    /// </summary>
    /// <remarks>
    /// При переходе к релизной версии убирается вычёркиванием его строки из
    /// <see cref="Sections" /> — данные под ним (<c>storm capabilities</c>) остаются.
    /// </remarks>
    public const string Development = "/dev";

    /// <summary>
    /// Разделы в порядке показа, разложенные по группам.
    /// </summary>
    /// <remarks>
    /// Восемнадцать разделов сплошным списком не читаются: чтобы понять, куда идти,
    /// приходилось прочитать их все. Группы отвечают на первый вопрос оператора —
    /// «это про мою сеть или про интернет» — и повторяют деление, которое и так есть
    /// в путях разделов.
    /// </remarks>
    public static IReadOnlyList<NavigationSection> Sections { get; } = Grouped(
    [
        new(Dashboard, "Дашборд", "Состояние сети, активные мониторы, алерты, быстрый запуск.", null, "Обзор"),

        new(Discovery, "Обнаружение", "Сканирование подсети: какие узлы в ней есть.", null, "Своя сеть"),
        new(Devices, "Устройства", "Инвентарь: адреса, MAC, производитель, имена и что изменилось.", null, "Своя сеть"),
        new(Topology, "Карта сети", "Граф сети с видимым различием подтверждённого и выведенного.", null, "Своя сеть"),
        new(LocalTests, "Локальные тесты", "Ping, TCP и UDP до машин своей сети; скорость между точками и сравнение резолверов DNS.", null, "Своя сеть"),
        new(Latency, "Задержка", "Непрерывный ping с живым графиком, джиттером и PDV.", null, "Своя сеть"),

        new(Path, "Анализ пути", "Traceroute и непрерывный MTR: потери и задержка по каждому хопу.", null, "Интернет"),
        new(Speed, "Скорость и качество", "Скорость наружу, мост к iperf3, задержка под нагрузкой.", null, "Интернет"),
        new(Inspect, "Инспекторы", "DNS, TLS и HTTP: разбор ответов и таймингов. Взгляд снаружи.", null, "Интернет"),
        new(Probes, "Внешние пробы", "Сценарии из цепочки шагов с разбивкой по фазам и порогами.", null, "Интернет"),

        new(Monitors, "Мониторы", "Постоянные проверки, доступность, SLA.", null, "Наблюдение"),
        new(Schedule, "Расписание", "Периодические запуски и окна обслуживания.", null, "Наблюдение"),
        new(Alerts, "Алерты", "Лента событий и состояние каналов доставки.", null, "Наблюдение"),

        new(Runs, "Журнал", "История прогонов с разбором до сырых измерений.", null, "Измеренное"),
        new(Presets, "Библиотека", "Пресеты: именованные тесты, которые можно повторить и передать.", null, "Измеренное"),
        new(Reports, "Отчёты", "Технический, сводка, акт тестирования и SLA. С методиками и условиями.", null, "Измеренное"),

        new(Settings, "Настройки", "Профили окружения, учётные данные, агенты, хранилище.", null, "Система"),

        // Временный раздел (И-24): уходит из релизной версии удалением этой строки.
        new(Development, "Разработка", "Сводка возможностей машины. Временный раздел — уйдёт из релиза.", null, "Система"),
    ]);

    /// <summary>Проставляет заголовок первому разделу каждой группы.</summary>
    private static List<NavigationSection> Grouped(IReadOnlyList<NavigationSection> sections)
    {
        var result = new List<NavigationSection>(sections.Count);
        var previous = string.Empty;

        foreach (var section in sections)
        {
            result.Add(section.Group == previous
                ? section
                : section with { GroupHeader = section.Group });

            previous = section.Group;
        }

        return result;
    }
}
