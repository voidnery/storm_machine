using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Presets;
using StormMachine.Application.Runs;
using StormMachine.Application.Topology;
using StormMachine.Discovery;
using StormMachine.Platform;
using StormMachine.Platform.Geo;
using StormMachine.Probes;
using StormMachine.Reporting;
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
        services.AddSingleton<RunOrchestrator>();

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

        // Карта сети своих измерений не делает: складывает инвентарь, трассировки
        // и сетевое окружение, поэтому пересчитывается мгновенно.
        services.AddSingleton<TopologyService>();

        // Отчёты. Движок PDF спрятан за IReportRenderer — замена стоит день, а не месяц.
        services.AddSingleton<IReportRenderer, PdfReportRenderer>();

        // Платформа
        services.AddSingleton<IHighResolutionClock, HighResolutionClock>();
        services.AddSingleton<INetworkEnvironment, WindowsNetworkEnvironment>();
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
