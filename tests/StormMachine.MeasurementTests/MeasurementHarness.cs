using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Composition;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Targets;

namespace StormMachine.MeasurementTests;

/// <summary>
/// Общая обвязка тестов точности: собранное ядро и прогон пробы по loopback.
/// </summary>
/// <remarks>
/// Цель всегда 127.0.0.1. Так тесты не зависят от того, какая сеть вокруг, и одинаково
/// работают на машине разработчика и на сборочном агенте.
/// </remarks>
internal static class MeasurementHarness
{
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddStormMachine();
        return services.BuildServiceProvider();
    }

    public static ProbeRequest LoopbackRequest(int count, int intervalMs = 1) => new()
    {
        Target = Target.Ip("127.0.0.1"),
        Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = count,
            ["interval"] = intervalMs,
            ["size"] = 32,
            ["timeout"] = 1000,
        },
    };

    public static async Task<List<Sample>> RunAsync(
        IServiceProvider services,
        ProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        var registry = services.GetRequiredService<IProbeRegistry>();
        Assert.True(registry.TryGet("ping", out var probe), "Проба ping не зарегистрирована");

        var samples = new List<Sample>();
        await foreach (var sample in probe.ExecuteAsync(request, cancellationToken))
        {
            samples.Add(sample);
        }

        return samples;
    }
}
