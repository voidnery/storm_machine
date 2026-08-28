using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.Application;
using StormMachine.Agents;
using StormMachine.Alerting;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Capabilities;
using StormMachine.Application.Probes;
using StormMachine.Application.Presets;
using StormMachine.Application.Profiles;
using StormMachine.Application.Snmp;
using StormMachine.Application.Runs;
using StormMachine.Application.Scenarios;
using StormMachine.Application.Monitors;
using StormMachine.Application.Topology;
using StormMachine.Discovery;
using StormMachine.Platform;
using StormMachine.Platform.Geo;
using StormMachine.Probes;
using StormMachine.Reporting;
using StormMachine.Snmp;
using StormMachine.Storage;

namespace StormMachine.Composition;

/// <summary>
/// Сборка продукта из слоёв.
/// </summary>
/// <remarks>
/// Клиенты вызывают только этот метод и не ссылаются на инфраструктуру напрямую —
/// правило 3 из docs/ARCHITECTURE.md §3. Когда появится серверный вариант, он соберёт
/// то же самое тем же вызовом.
/// </remarks>
public static class StormMachineServiceCollectionExtensions
{
    public static IServiceCollection AddStormMachine(this IServiceCollection services) =>
        services.AddStormMachine(new StorageOptions());

    public static IServiceCollection AddStormMachine(this IServiceCollection services, StorageOptions storage)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storage);

        services.AddStormMachineApplication();

        // Хранилище
        services.AddSingleton(storage);
        services.AddSingleton<IRunStore>(provider => new SqliteRunStore(
            provider.GetRequiredService<StorageOptions>(),
            provider.GetService<ILogger<SqliteRunStore>>()));
        // Где лежат данные — вопрос, который задают, когда что-то ведёт себя странно.
        // Отвечать на него должен сам продукт, а не догадки.
        services.AddSingleton<IStorageLocation>(p => (SqliteRunStore)p.GetRequiredService<IRunStore>());

        // Профили сетевого окружения: где мы находимся. Пишутся в условия каждого
        // измерения — замер у заказчика и замер в офисе несопоставимы.
        services.AddSingleton<IProfileStore>(provider => new SqliteProfileStore(
            (SqliteRunStore)provider.GetRequiredService<IRunStore>()));

        services.AddSingleton<ProfileService>();
        services.AddSingleton<RunOrchestrator>();

        // Уровень 1. Опрос оборудования по SNMP: только чтение, только теми учётными
        // данными, которые оператор завёл сам. Библиотека проверена спайком-08
        // на совместимость с обрезкой публикации.
        services.AddSingleton<ISnmpCredentialStore>(provider => new SqliteSnmpCredentialStore(
            (SqliteRunStore)provider.GetRequiredService<IRunStore>(),
            provider.GetRequiredService<ISecretProtector>()));

        services.AddSingleton<ISnmpClient, SharpSnmpClient>();
        services.AddSingleton<SnmpService>();

        // Библиотека пресетов делит базу с журналом, поэтому строится поверх него.
        services.AddSingleton<IPresetStore>(provider => new SqlitePresetStore(
            (SqliteRunStore)provider.GetRequiredService<IRunStore>()));
        services.AddSingleton<PresetService>();

        // Инвентарь делит файл с журналом и библиотекой: заводить вторую базу значило бы
        // получить два места, которые надо раздельно чинить, переносить и подчищать.
        services.AddSingleton<IDeviceStore>(provider => new SqliteDeviceStore(
            (SqliteRunStore)provider.GetRequiredService<IRunStore>()));

        // Обнаружение. База OUI встроена в сборку — вендор по MAC входит в уровень 0.
        services.AddSingleton<IArpResolver, WindowsArpResolver>();
        services.AddSingleton<IOuiCatalog, OuiCatalog>();
        services.AddSingleton<IDiscoveryService, DiscoveryService>();

        // Раскладка карты — одна на продукт. Полотно на экране и схема в отчёте
        // обязаны показывать одну и ту же сеть одинаково.
        services.AddSingleton<ITopologyLayout, MsaglTopologyLayout>();

        // Карта сети своих измерений не делает: складывает инвентарь, трассировки
        // и сетевое окружение, поэтому пересчитывается мгновенно.
        services.AddSingleton<TopologyService>();

        // Сценарий своих измерений не делает: вызывает те же пробы через тот же
        // оркестратор, поэтому каждый шаг попадает в журнал обычным прогоном.
        services.AddSingleton<ScenarioRunner>();

        // Взгляд снаружи: единственная возможность, обязательно обращающаяся к чужим
        // серверам. Изнутри сети внешний адрес и поведение NAT неизвестны в принципе.
        services.AddSingleton<IOutsideView, OutsideViewService>();

        // Агенты. Личность клиента и сопряжения живут в той же базе, что и журнал:
        // резервная копия одного файла обязана возвращать работающую установку целиком.
        services.AddSingleton<IAgentStore>(provider => new SqliteAgentStore(
            (SqliteRunStore)provider.GetRequiredService<IRunStore>()));

        services.AddSingleton<AgentDirectory>();
        services.AddSingleton<IAgentDirectory>(p => p.GetRequiredService<AgentDirectory>());

        // Настройки и секреты. Появились ради каналов оповещения: адрес webhook
        // и пароль от почты негде было держать. Пароль шифруется средствами Windows —
        // базу копируют и присылают в поддержку, и открытым текстом он там оказаться
        // не должен.
        services.AddSingleton<ISecretProtector, WindowsSecretProtector>();
        services.AddSingleton<ISettingsStore>(provider => new SqliteSettingsStore(
            (SqliteRunStore)provider.GetRequiredService<IRunStore>(),
            provider.GetRequiredService<ISecretProtector>()));

        // Мониторы. Расписание, состояние, проверки и лента алертов — в той же базе.
        services.AddSingleton<IMonitorStore>(provider => new SqliteMonitorStore(
            (SqliteRunStore)provider.GetRequiredService<IRunStore>()));

        // Часы отдельной зависимостью: без этого поведение «машина спала восемь часов»
        // нельзя было бы проверить тестом, а оно и есть предмет приёмки И-14.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MonitorService>();
        services.AddSingleton<MonitorScheduler>();

        // Каналы, работающие без человека у экрана. Звук и значок в трее регистрирует
        // графический клиент: без окна они не значат ничего.
        services.AddSingleton<IAlertChannel, WebhookAlertChannel>();
        services.AddSingleton<IAlertChannel, EmailAlertChannel>();
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

        // Эталоны: снимок измерения вместе с условиями, при которых он снят.
        services.AddSingleton<IBaselineStore>(provider => new SqliteBaselineStore(
            (SqliteRunStore)provider.GetRequiredService<IRunStore>()));

        // Отчёты. Движок PDF спрятан за IReportRenderer — замена стоит день, а не месяц.
        services.AddSingleton<IReportRenderer, PdfReportRenderer>();

        // Выгрузка отдельно от отчёта: отчёт объясняет, выгрузка отдаёт.
        services.AddSingleton<IRunExporter, RunExporter>();

        // Платформа
        services.AddSingleton<IHighResolutionClock, HighResolutionClock>();
        services.AddSingleton<INetworkEnvironment, WindowsNetworkEnvironment>();

        // Что позволяет сама машина: права, драйвер захвата, сырые сокеты.
        // Определяется проверкой, а не предположением по одному флагу прав.
        services.AddSingleton<ISystemCapabilities, WindowsSystemCapabilities>();
        services.AddSingleton<CapabilityInspector>();
        services.AddSingleton<TargetResolver>();

        // Обогащение маршрута. База принадлежности адресов не входит в поставку —
        // её лицензия несовместима с MIT, поэтому оператор кладёт файл сам, а продукт
        // работает и без него.
        services.AddSingleton<IAsnDatabase>(_ => AsnDatabase.Open());
        services.AddSingleton<IHopAnnotator, HopAnnotator>();

        // Пробы. Порядок регистрации определяет порядок в `storm probes`:
        // сначала скалярные серии, затем инспекторы, затем анализ пути.
        services.AddSingleton<IProbe, IcmpProbe>();
        services.AddSingleton<IProbe, TcpConnectProbe>();
        services.AddSingleton<IProbe, UdpProbe>();
        services.AddSingleton<IProbe, DnsProbe>();
        services.AddSingleton<IProbe, TlsProbe>();
        services.AddSingleton<IProbe, HttpProbe>();
        services.AddSingleton<IProbe, TracerouteProbe>();

        // Скорость наружу: единственная проба, которой не нужен агент. Она отвечает
        // на другой вопрос — «что нам продают», а не «что между этими двумя точками».
        services.AddSingleton<IProbe, SpeedtestProbe>();

        // Мост туда, где своего агента поставить нельзя, и одновременно проверка себя:
        // две реализации на одном канале обязаны сходиться.
        services.AddSingleton<IProbe, Iperf3Probe>();

        // Удалённое измерение — обычная проба: попадает в журнал, открывается в отчёте
        // и годится шагом сценария ровно как ping. Отдельный путь для неё означал бы
        // второй продукт рядом с первым.
        services.AddSingleton<IProbe, ThroughputProbe>();
        services.AddSingleton<IProbe, ChannelQualityProbe>();
        // Отложенный реестр: проба задержки под нагрузкой сама лежит в реестре
        // и при этом просит его. Зависимость круговая по существу — она разрывается
        // тем, что реестр нужен не при сборке, а при запуске измерения.
        services.AddSingleton(provider =>
            new Lazy<IProbeRegistry>(provider.GetRequiredService<IProbeRegistry>));

        services.AddSingleton<IProbe, BufferbloatProbe>();

        return services;
    }

    /// <summary>
    /// Готовит ядро к измерениям: калибрует порог разрешения таймера.
    /// Вызывается один раз при запуске клиента.
    /// </summary>
    public static async Task InitializeStormMachineAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var store = services.GetRequiredService<IRunStore>();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var clock = services.GetRequiredService<IHighResolutionClock>();
        await clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
