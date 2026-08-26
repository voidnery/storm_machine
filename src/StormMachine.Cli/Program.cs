using System.CommandLine;
using System.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.Application;
using StormMachine.Application.Probes;
using StormMachine.Cli.Commands;
using StormMachine.Composition;

namespace StormMachine.Cli;

/// <summary>
/// Точка входа консольного клиента.
/// </summary>
/// <remarks>
/// CLI — не вспомогательная утилита, а доказательство того, что ядро не зависит
/// от интерфейса: он собирает те же службы тем же вызовом, что и графический клиент.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Измерения чувствительны к паузам сборщика мусора. Режим низких задержек
        // не отменяет сборку, но делает паузы короче и реже — на стенде за 300 проб
        // их не случилось ни одной.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        using var services = BuildServiceProvider();
        var root = BuildRootCommand(services);

        return root.Parse(args).Invoke();
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        services.AddStormMachine();

        return services.BuildServiceProvider();
    }

    private static RootCommand BuildRootCommand(IServiceProvider services)
    {
        var root = new RootCommand($"{ProductInfo.Name} — станция тестирования и диагностики сетей.");

        // Команды не перечисляются вручную: каждая проба объявляет своё имя и параметры,
        // и команда строится по объявлению. Новая проба появляется в CLI сама.
        foreach (var probe in services.GetRequiredService<IEnumerable<IProbe>>())
        {
            root.Subcommands.Add(ProbeCommandFactory.Create(services, probe));
        }

        root.Subcommands.Add(PresetsCommand.Create(services));
        root.Subcommands.Add(RunsCommand.Create(services));
        root.Subcommands.Add(EnvCommand.Create(services));
        root.Subcommands.Add(BuildProbesCommand(services));
        root.Subcommands.Add(BuildAboutCommand());

        return root;
    }

    private static Command BuildProbesCommand(IServiceProvider services)
    {
        var command = new Command("probes", "Показать доступные пробы.");

        command.SetAction(_ =>
        {
            var registry = services.GetRequiredService<IProbeRegistry>();

            if (registry.Descriptors.Count == 0)
            {
                Console.WriteLine("Пробы ещё не зарегистрированы.");
                return 0;
            }

            foreach (var descriptor in registry.Descriptors)
            {
                Console.WriteLine($"{descriptor.Name}  —  {descriptor.Title}");
                Console.WriteLine($"    {descriptor.Description}");
                Console.WriteLine($"    методика: {descriptor.Methodology}");
                Console.WriteLine($"    права администратора: {(descriptor.RequiresElevation ? "нужны" : "не нужны")}");
                Console.WriteLine($"    параметры: {string.Join(", ", descriptor.Parameters.Select(p => p.Name))}");
                Console.WriteLine();
            }

            return 0;
        });

        return command;
    }

    private static Command BuildAboutCommand()
    {
        var command = new Command("about", "Версия, лицензия и ссылки.");

        command.SetAction(_ =>
        {
            Console.WriteLine(ProductInfo.NameAndVersion);
            Console.WriteLine("Лицензия: MIT");
            Console.WriteLine("Репозиторий: https://github.com/voidnery/storm_machine");
            Console.WriteLine();
            Console.WriteLine("Данные ASN и геолокации — DB-IP (CC BY-SA 4.0).");
            return 0;
        });

        return command;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var error = e.ExceptionObject as Exception;
        Console.Error.WriteLine($"Необработанная ошибка: {error?.Message}");
        Console.Error.WriteLine(error?.StackTrace);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Console.Error.WriteLine($"Необработанная ошибка в фоновой задаче: {e.Exception.Message}");

        // Одна упавшая проба не должна ронять прогон — требование отказоустойчивости
        // (docs/01-analysis.md §7).
        e.SetObserved();
    }
}
