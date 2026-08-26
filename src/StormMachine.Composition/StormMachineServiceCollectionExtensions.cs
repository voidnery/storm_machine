using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Platform;
using StormMachine.Probes;
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

        // Платформа
        services.AddSingleton<IHighResolutionClock, HighResolutionClock>();
        services.AddSingleton<INetworkEnvironment, WindowsNetworkEnvironment>();
        services.AddSingleton<TargetResolver>();

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
