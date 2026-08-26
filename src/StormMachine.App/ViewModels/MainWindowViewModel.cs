using CommunityToolkit.Mvvm.ComponentModel;
using StormMachine.Application;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Оболочка главного окна: боковая навигация и строка состояния.
/// </summary>
/// <remarks>
/// В итерации И-0 разделы — заглушки с честной пометкой, в какой итерации каждый появится.
/// Первый рабочий экран (ping-монитор) приходит в И-4.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel()
    {
        Sections =
        [
            new("/", "Дашборд", "Состояние сети, активные мониторы, алерты, быстрый запуск.", "И-4"),
            new("/local/topology", "Карта сети", "Граф топологии с редактором и отметками достоверности связей.", "И-9"),
            new("/local/devices", "Устройства", "Инвентарь: адреса, MAC, вендор, имена, история.", "И-8"),
            new("/local/discovery", "Обнаружение", "Сканирование подсети, история сканов, различия между ними.", "И-8"),
            new("/local/tests", "Локальные тесты", "Доступность, задержка, джиттер, скорость, bufferbloat, сервисы.", "И-4"),
            new("/internet/probes", "Внешние пробы", "Сценарии probe до узлов в интернете.", "И-11"),
            new("/internet/path", "Анализ пути", "Traceroute и непрерывный MTR с потерями по хопам.", "И-7"),
            new("/internet/speed", "Скорость и качество", "Speedtest, bufferbloat, IPv6, тип NAT.", "И-13"),
            new("/internet/inspect", "Инспекторы", "DNS, TLS и HTTP: разбор ответов и таймингов.", "И-11"),
            new("/monitors", "Мониторы", "Постоянные проверки, доступность, SLA.", "И-14"),
            new("/presets", "Библиотека", "Пресеты и наборы тестов.", "И-5"),
            new("/schedule", "Расписание", "Периодические запуски и окна обслуживания.", "И-14"),
            new("/runs", "Журнал", "История прогонов с разбором до сырых измерений.", "И-3"),
            new("/reports", "Отчёты", "Формирование PDF с методиками и условиями измерения.", "И-6"),
            new("/alerts", "Алерты", "Правила и лента событий.", "И-14"),
            new("/settings", "Настройки", "Профили сети, агенты, учётные данные, хранилище.", "И-16"),
        ];

        SelectedSection = Sections[0];
    }

    public IReadOnlyList<NavigationSection> Sections { get; }

    [ObservableProperty]
    private NavigationSection? _selectedSection;

    public static string WindowTitle => ProductInfo.NameAndVersion;

    /// <summary>
    /// Заглушка строки состояния. В И-1 сюда придут настоящие данные об адаптере,
    /// включая предупреждение о виртуальном коммутаторе или VPN.
    /// </summary>
    public static string StatusText =>
        "Итерация И-0 — каркас. Сетевой адаптер будет определяться с И-1.";

    public static string LevelText => "Уровень 0 — ядро";
}
