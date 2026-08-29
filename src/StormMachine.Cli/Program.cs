using System.CommandLine;
using System.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Capabilities;
using StormMachine.Cli.Commands;
using StormMachine.Cli.Rendering;
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
    /// <summary>
    /// Точка входа.
    /// </summary>
    /// <remarks>
    /// Асинхронная не для красоты: планировщик мониторов освобождается только
    /// асинхронно — он останавливает свой цикл и дожидается идущих проверок.
    /// Синхронное закрытие контейнера на таком типе падает с прямым указанием
    /// использовать DisposeAsync, и это правильно: обрывать проверку на полуслове
    /// значило бы потерять уже измеренное.
    /// </remarks>
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Измерения чувствительны к паузам сборщика мусора. Режим низких задержек
        // не отменяет сборку, но делает паузы короче и реже — на стенде за 300 проб
        // их не случилось ни одной.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Путь к базе разбирается до сборки служб: хранилище нужно уже собранным,
        // а System.CommandLine разбирает строку позже. Предварительный просмотр
        // аргументов — цена за то, чтобы ключ работал, а не только значился в справке.
        ApplyDatabaseOverride(args);

        await using var services = BuildServiceProvider();
        var root = BuildRootCommand(services);

        // Ответные файлы System.CommandLine выключены: разбор @файла у нас свой —
        // это список целей, а не список аргументов. Со встроенным разбором строка
        // «storm scenario run voice @цели.txt» превращалась в подстановку аргументов
        // из файла и падала с непонятной ошибкой разбора.
        var parsing = new ParserConfiguration { ResponseFileTokenReplacer = null };

        return await root.Parse(args, parsing).InvokeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Переносит «--база» в переменную окружения до сборки служб.
    /// </summary>
    /// <remarks>
    /// Явный ключ побеждает переменную окружения: то, что человек написал в этой
    /// команде, весомее того, что осталось в окружении с прошлого раза.
    /// </remarks>
    private static void ApplyDatabaseOverride(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--база" or "--db")
            {
                Environment.SetEnvironmentVariable(StorageEnvironment.PathVariable, args[i + 1]);

                return;
            }
        }
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

        // Канал терминала регистрируется клиентом, а не корнем композиции: писать
        // в консоль имеет смысл только там, где она есть. У графического клиента
        // по той же причине свои каналы — звук и значок в трее.
        services.AddSingleton<IAlertChannel, ConsoleAlertChannel>();

        return services.BuildServiceProvider();
    }

    private static RootCommand BuildRootCommand(IServiceProvider services)
    {
        var root = new RootCommand($"{ProductInfo.Name} — станция тестирования и диагностики сетей.");

        // Объявлена, чтобы попасть в справку и не считаться неизвестным ключом.
        // Значение уже прочитано до сборки служб — см. DatabaseOverride.
        root.Options.Add(new Option<string?>("--база", "--db")
        {
            Description = "Работать с другим файлом базы. То же самое делает переменная "
                          + "окружения STORM_DB. Полезно для копии из поддержки и для проверок, "
                          + "которые не должны подмешиваться в рабочую историю.",
            Recursive = true,
        });

        // Команды не перечисляются вручную: каждая проба объявляет своё имя и параметры,
        // и команда строится по объявлению. Новая проба появляется в CLI сама.
        foreach (var probe in services.GetRequiredService<IEnumerable<IProbe>>())
        {
            root.Subcommands.Add(ProbeCommandFactory.Create(services, probe));
        }

        root.Subcommands.Add(DiscoverCommand.CreateDiscover(services));
        root.Subcommands.Add(DevicesCommand.Create(services));
        root.Subcommands.Add(TopologyCommand.Create(services));
        root.Subcommands.Add(ScenarioCommand.Create(services));
        root.Subcommands.Add(OutsideCommand.Create(services));
        root.Subcommands.Add(AgentsCommand.Create(services));
        root.Subcommands.Add(PresetsCommand.Create(services));
        root.Subcommands.Add(MonitorsCommand.Create(services));
        root.Subcommands.Add(AlertsCommand.Create(services));
        root.Subcommands.Add(RunsCommand.Create(services));
        root.Subcommands.Add(ReportCommand.Create(services));
        root.Subcommands.Add(BaselineCommand.Create(services));
        root.Subcommands.Add(EnvCommand.Create(services));
        root.Subcommands.Add(ProfilesCommand.Create(services));
        root.Subcommands.Add(SnmpCommand.Create(services));
        root.Subcommands.Add(CaptureCommand.Create(services));
        root.Subcommands.Add(BuildProbesCommand(services));
        root.Subcommands.Add(BuildCapabilitiesCommand(services));
        root.Subcommands.Add(BuildAboutCommand(services));

        return root;
    }

    /// <summary>
    /// Что продукт может на этой машине: <c>storm capabilities</c>.
    /// </summary>
    /// <remarks>
    /// Первый вопрос при знакомстве с инструментом — «что заработает сразу, а за что
    /// придётся платить установкой драйверов и выпрашиванием паролей у сетевиков».
    /// Ответ на него продукт обязан давать сам, а не оставлять читателю документации.
    /// </remarks>
    private static Command BuildCapabilitiesCommand(IServiceProvider services)
    {
        var verbose = new Option<bool>("--подробно", "--verbose")
        {
            Description = "Показать пояснение к каждой возможности, а не только к проблемным.",
        };

        var command = new Command("capabilities", "Что продукт может на этой машине и чего не может.")
        {
            verbose,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var inspector = services.GetRequiredService<CapabilityInspector>();
            var report = await inspector.InspectAsync(cancellationToken).ConfigureAwait(false);

            CapabilityRenderer.Write(report, parse.GetValue(verbose));

            return 0;
        });

        return command;
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

    private static Command BuildAboutCommand(IServiceProvider services)
    {
        var command = new Command("about", "Версия, лицензия, ссылки и где лежат данные.");

        command.SetAction(_ =>
        {
            Console.WriteLine(ProductInfo.NameAndVersion);
            Console.WriteLine("Лицензия: MIT");
            Console.WriteLine("Репозиторий: https://github.com/voidnery/storm_machine");
            Console.WriteLine();
            Console.WriteLine("Данные ASN и геолокации — DB-IP (CC BY-SA 4.0).");

            // Путь к базе — не справочная мелочь. Когда сопряжение пропало или журнал
            // пуст, первый вопрос всегда один: с каким файлом продукт разговаривает.
            // Без ответа человек разбирается догадками.
            Console.WriteLine();
            Console.WriteLine($"База данных: {services.GetRequiredService<IStorageLocation>().DatabasePath}");
            Console.WriteLine("В ней журнал прогонов, пресеты, инвентарь, карта, сопряжения с агентами,");
            Console.WriteLine("личность клиента, мониторы с историей проверок, лента алертов и настройки.");
            Console.WriteLine("Резервная копия этого файла возвращает установку целиком.");
            Console.WriteLine();
            Console.WriteLine("Пароли каналов оповещения зашифрованы средствами Windows и привязаны");
            Console.WriteLine("к учётной записи: на другой машине их придётся задать заново.");

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
