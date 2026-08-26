using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Probes;

namespace StormMachine.Application;

/// <summary>
/// Точка сборки слоя приложения.
/// </summary>
/// <remarks>
/// Слой приложения не знает, кто его вызывает: GUI, командная строка или — в будущем —
/// сетевой API. Все три потребителя собирают одни и те же службы через этот метод
/// (принцип 2, docs/01-analysis.md §8.2).
/// </remarks>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStormMachineApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProbeRegistry, ProbeRegistry>();

        return services;
    }
}

/// <summary>Реестр проб, собираемый из всех зарегистрированных реализаций <see cref="IProbe"/>.</summary>
internal sealed class ProbeRegistry : IProbeRegistry
{
    private readonly Dictionary<string, IProbe> _byName;

    public ProbeRegistry(IEnumerable<IProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);

        _byName = probes.ToDictionary(p => p.Descriptor.Name, StringComparer.OrdinalIgnoreCase);
        Descriptors = [.. _byName.Values.Select(p => p.Descriptor).OrderBy(d => d.Name, StringComparer.Ordinal)];
    }

    public IReadOnlyList<ProbeDescriptor> Descriptors { get; }

    public bool TryGet(string name, out IProbe probe) => _byName.TryGetValue(name, out probe!);
}
