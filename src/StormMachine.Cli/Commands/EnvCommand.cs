using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Runs;
using StormMachine.Domain.Measurements;

namespace StormMachine.Cli.Commands;

/// <summary>
/// <c>storm env</c> — сетевое окружение и пригодность адаптеров для измерений.
/// </summary>
internal static class EnvCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("env", "Сетевые адаптеры, их тип и пригодность для измерений.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var environment = services.GetRequiredService<INetworkEnvironment>();
            var clock = services.GetRequiredService<IHighResolutionClock>();

            // Порог разрешения показываем измеренный, а не нулевой.
            await clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);

            var adapters = environment.GetAdapters()
                .Where(a => a.IsUp && a.IPv4Address is not null)
                .ToList();

            var primary = environment.GetPrimaryAdapter();

            Console.WriteLine($"Права администратора : {(environment.IsElevated ? "есть" : "нет (уровень 0 их и не требует)")}");
            Console.WriteLine($"Разрешение таймера   : {clock.ResolutionNanoseconds:0.###} нс");
            Console.WriteLine($"Порог разрешения     : {clock.CalibrationBaselineMs:0.000} мс (измерено на loopback)");
            Console.WriteLine();

            if (adapters.Count == 0)
            {
                Console.WriteLine("Активных адаптеров с адресом IPv4 не найдено.");
                return 0;
            }

            foreach (var adapter in adapters)
            {
                var isPrimary = primary is not null && primary.Id == adapter.Id;
                Console.WriteLine($"{(isPrimary ? "→ " : "  ")}{adapter.Name}");
                Console.WriteLine($"     тип      : {Describe(adapter.Kind)}");
                Console.WriteLine($"     адрес    : {adapter.SubnetCidr ?? adapter.IPv4Address}");

                if (adapter.Gateways.Count > 0)
                {
                    Console.WriteLine($"     шлюз     : {string.Join(", ", adapter.Gateways)}");
                }

                if (adapter.SpeedBitsPerSecond > 0)
                {
                    Console.WriteLine($"     скорость : {adapter.SpeedBitsPerSecond / 1_000_000} Мбит/с");
                }

                Console.WriteLine($"     MAC      : {adapter.MacAddress ?? "—"}");
                Console.WriteLine();
            }

            if (primary is not null)
            {
                var context = ContextFor(primary, clock);
                if (context.TimingWarning is { } warning)
                {
                    Console.WriteLine("ВНИМАНИЕ");
                    Console.WriteLine($"  {warning}");
                    Console.WriteLine();
                }
            }

            return 0;
        });

        return command;
    }

    private static MeasurementContext ContextFor(NetworkAdapter adapter, IHighResolutionClock clock) =>
        MeasurementConditions.Build(adapter, clock, Methodology.Unspecified);

    /// <summary>Тип адаптера словами продукта — словарь один на консоль, окно и отчёт.</summary>
    public static string Describe(AdapterKind kind) => AdapterWording.Kind(kind);
}
