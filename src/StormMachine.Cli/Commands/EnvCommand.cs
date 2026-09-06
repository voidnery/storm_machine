using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Adapters;
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

        // Выбор адаптера — настройка машины, а не окна: сделанный мышью, он обязан
        // действовать и в консоли, и наоборот. Хранится он в базе, и обе стороны
        // читают одно и то же значение.
        var choose = new Option<string?>("--выбрать", "--choose")
        {
            Description = "Меряться от этого адаптера: имя или опознание из списка ниже.",
        };

        var auto = new Option<bool>("--авто", "--auto")
        {
            Description = "Вернуть автоматический выбор — по маршруту по умолчанию.",
        };

        command.Options.Add(choose);
        command.Options.Add(auto);

        command.SetAction(async (result, cancellationToken) =>
        {
            var environment = services.GetRequiredService<INetworkEnvironment>();
            var clock = services.GetRequiredService<IHighResolutionClock>();
            var choice = services.GetRequiredService<AdapterChoice>();

            await choice.LoadAsync(cancellationToken).ConfigureAwait(false);

            if (result.GetValue(auto))
            {
                await choice.ChooseAsync(null, cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Адаптер выбирается автоматически — по маршруту по умолчанию.");
                Console.WriteLine();
            }
            else if (result.GetValue(choose) is { Length: > 0 } wanted)
            {
                var found = environment.GetAdapters().FirstOrDefault(a =>
                    string.Equals(a.Id, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase));

                if (found is null)
                {
                    // Отказ, а не молчаливое сохранение: сохранённое опознание
                    // несуществующего адаптера выглядело бы как сделанный выбор,
                    // а меряли бы по-прежнему по маршруту.
                    Console.Error.WriteLine($"Адаптера «{wanted}» нет. Список — «storm env» без ключей.");
                    return 1;
                }

                await choice.ChooseAsync(found.Id, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Меряем от «{found.Name}».");
                Console.WriteLine();
            }

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

            // Расхождение выбора с маршрутом называется вслух и здесь: консоль
            // и окно об одном и том же говорят одинаково.
            if (services.GetRequiredService<ChosenAdapterEnvironment>().Notice is { } notice)
            {
                Console.WriteLine("ВНИМАНИЕ");
                Console.WriteLine($"  {notice}");
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
