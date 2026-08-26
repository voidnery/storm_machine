using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.Application;
using StormMachine.Application.Probes;

namespace StormMachine.Cli;

/// <summary>
/// Точка входа консольного клиента.
/// </summary>
/// <remarks>
/// В итерации И-0 команд измерения ещё нет — есть оболочка, корень композиции
/// и обработка ошибок. Пробы появятся в И-1 (ICMP) и И-2 (остальные).
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddStormMachineApplication();

        return services.BuildServiceProvider();
    }

    private static RootCommand BuildRootCommand(IServiceProvider services)
    {
        var root = new RootCommand($"{ProductInfo.Name} — станция тестирования и диагностики сетей.")
        {
            BuildProbesCommand(services),
            BuildAboutCommand(),
        };

        return root;
    }

    /// <summary>Перечисляет пробы, зарегистрированные в ядре. В И-0 список пуст — это ожидаемо.</summary>
    private static Command BuildProbesCommand(IServiceProvider services)
    {
        var command = new Command("probes", "Показать доступные пробы.");

        command.SetAction(_ =>
        {
            var registry = services.GetRequiredService<IProbeRegistry>();

            if (registry.Descriptors.Count == 0)
            {
                Console.WriteLine("Пробы ещё не зарегистрированы.");
                Console.WriteLine("Первая появится в итерации И-1 — ICMP.");
                return 0;
            }

            Console.WriteLine($"{"ИМЯ",-12} {"ЕДИНИЦЫ",-18} МЕТОДИКА");
            foreach (var d in registry.Descriptors)
            {
                Console.WriteLine($"{d.Name,-12} {d.Unit,-18} {d.Methodology}");
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

        // Помечаем как обработанную: одна упавшая проба не должна ронять весь прогон
        // (требование отказоустойчивости, docs/01-analysis.md §7).
        e.SetObserved();
    }
}
