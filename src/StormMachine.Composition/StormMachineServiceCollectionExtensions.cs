using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Platform;
using StormMachine.Probes;

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
    public static IServiceCollection AddStormMachine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddStormMachineApplication();

        // Платформа
        services.AddSingleton<IHighResolutionClock, HighResolutionClock>();
        services.AddSingleton<INetworkEnvironment, WindowsNetworkEnvironment>();
        services.AddSingleton<TargetResolver>();

        // Пробы
        services.AddSingleton<IProbe, IcmpProbe>();

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

        var clock = services.GetRequiredService<IHighResolutionClock>();
        await clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
