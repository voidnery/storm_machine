using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Agents;
using StormMachine.Cli.Rendering;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Агенты: <c>storm agents</c>.
/// </summary>
/// <remarks>
/// Два способа сопряжения отражают решение оператора, принятое перед И-12: соединение
/// устанавливает любая сторона. Который выбрать, определяет не удобство, а права
/// на площадке: <c>pair &lt;адрес&gt;</c> требует разрешённых входящих там,
/// <c>pair --ждать</c> — здесь.
/// </remarks>
internal static class AgentsCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("agents", "Удалённые точки измерения: сопряжение, список, проверка связи.");

        command.Subcommands.Add(BuildBrowse(services));
        command.Subcommands.Add(BuildPair(services));
        command.Subcommands.Add(BuildCheck(services));
        command.Subcommands.Add(BuildRename(services));
        command.Subcommands.Add(BuildForget(services));
        command.Subcommands.Add(BuildKit(services));

        command.SetAction(async (_, cancellationToken) =>
        {
            var directory = services.GetRequiredService<IAgentDirectory>();

            AgentsRenderer.WriteList(
                await directory.ListAsync(cancellationToken).ConfigureAwait(false),
                await directory.GetOwnThumbprintAsync(cancellationToken).ConfigureAwait(false));

            return 0;
        });

        return command;
    }

    /// <summary>
    /// Комплект агента для другого конца: <c>storm agents kit</c>.
    /// </summary>
    /// <remarks>
    /// То же самое, что делает кнопка «Собрать комплект» в окне, и тем же сборщиком:
    /// консоль и окно обязаны уметь одно и то же, иначе площадка без графики теряет
    /// возможность, а не удобство.
    /// </remarks>
    private static Command BuildKit(IServiceProvider services)
    {
        var platformArgument = new Argument<string>("платформа")
        {
            Description = "Система того конца: win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64.",
        };

        var outOption = new Option<string?>("--куда", "--out")
        {
            Description = "Куда положить комплект. По умолчанию — в текущий каталог.",
        };

        var command = new Command("kit", "Собрать комплект агента для другого конца.")
        {
            platformArgument,
            outOption,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var wanted = parse.GetValue(platformArgument)!;

            var platform = AgentPlatforms.All
                .Select(p => (Platform: p, Rid: AgentPlatforms.Rid(p)))
                .FirstOrDefault(p => string.Equals(p.Rid, wanted, StringComparison.OrdinalIgnoreCase));

            if (platform.Rid is null)
            {
                Console.Error.WriteLine(
                    $"Платформа «{wanted}» неизвестна. Бывают: "
                    + string.Join(", ", AgentPlatforms.All.Select(AgentPlatforms.Rid)));

                return 1;
            }

            var binaries = services.GetRequiredService<IAgentBinaries>();

            if (!binaries.Has(platform.Platform))
            {
                Console.Error.WriteLine(
                    $"В поставке нет агента для «{AgentPlatforms.Title(platform.Platform)}». "
                    + "Агенты кладутся в подкаталог «agents» при сборке выпуска.");

                return 1;
            }

            var directory = services.GetRequiredService<IAgentDirectory>();
            var environment = services.GetRequiredService<INetworkEnvironment>();

            var path = parse.GetValue(outOption) is { Length: > 0 } chosen
                ? chosen
                : Path.Combine(Directory.GetCurrentDirectory(), AgentPlatforms.KitName(platform.Platform));

            try
            {
                var kit = await services.GetRequiredService<IAgentKitBuilder>()
                    .BuildAsync(
                        new AgentKitRequest
                        {
                            Platform = platform.Platform,
                            OutputPath = path,
                            ClientThumbprint = await directory
                                .GetOwnThumbprintAsync(cancellationToken)
                                .ConfigureAwait(false),
                            ClientAddresses = Addresses(environment),
                            Port = directory.DefaultPort,

                            // Имя машины приходит отсюда, а не из ядра: у сервера это чужая
                            // машина в стойке, и знать её имя ядру неоткуда.
                            ClientName = Environment.MachineName,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine($"Комплект собран: {kit.Path}");
                Console.WriteLine($"Размер: {kit.Bytes / 1024 / 1024} МБ");
                Console.WriteLine();
                Console.WriteLine("Внутри:");

                foreach (var item in kit.Contents)
                {
                    Console.WriteLine($"  {item}");
                }

                Console.WriteLine();
                Console.WriteLine("Увезите файл на тот конец и распакуйте. Что делать дальше —");
                Console.WriteLine($"написано внутри, в {IAgentKitBuilder.ReadmeName}.");

                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }
        });

        return command;
    }

    /// <summary>Адреса этой машины: по ним агент дозванивается обратно.</summary>
    private static IReadOnlyList<string> Addresses(INetworkEnvironment environment) =>
    [
        .. new[] { environment.GetPrimaryAdapter()?.IPv4Address }
            .Concat(environment.GetAdapters().Where(a => a.IsUp).Select(a => a.IPv4Address))
            .Where(a => a is { Length: > 0 } && a != "127.0.0.1")
            .Select(a => a!)
            .Distinct(StringComparer.Ordinal),
    ];

    private static Command BuildBrowse(IServiceProvider services)
    {
        var secondsOption = new Option<int>("--секунд", "--seconds")
        {
            Description = "Сколько слушать эфир.",
            DefaultValueFactory = _ => 5,
        };

        var command = new Command(
            "browse",
            "Послушать, кто объявляет о себе в этой подсети. Не заменяет сопряжение.")
        {
            secondsOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var directory = services.GetRequiredService<IAgentDirectory>();
            var seconds = Math.Clamp(parseResult.GetValue(secondsOption), 1, 60);

            Console.WriteLine($"Слушаю эфир {seconds} с…");

            try
            {
                AgentsRenderer.WriteDiscovered(
                    await directory.BrowseAsync(TimeSpan.FromSeconds(seconds), cancellationToken)
                        .ConfigureAwait(false));

                return 0;
            }
            catch (Exception ex) when (ex is AgentException or InvalidOperationException or IOException)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }
        });

        return command;
    }

    private static Command BuildPair(IServiceProvider services)
    {
        var addressArgument = new Argument<string?>("адрес")
        {
            Description = "Адрес агента. Не указан — ждём, пока агент позвонит сам.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var portOption = new Option<int>("--порт", "--port")
        {
            Description = "Порт управляющего канала.",
            DefaultValueFactory = _ => services.GetRequiredService<IAgentDirectory>().DefaultPort,
        };

        var codeOption = new Option<string?>("--код", "--code")
        {
            Description = "Код, выданный агентом. При ожидании код выдаём мы.",
        };

        var waitOption = new Option<bool>("--ждать", "--wait")
        {
            Description = "Ждать звонка агента. Нужно там, где на площадке нет прав открыть порт.",
        };

        var command = new Command("pair", "Сопрячься с агентом.")
        {
            addressArgument,
            portOption,
            codeOption,
            waitOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var directory = services.GetRequiredService<IAgentDirectory>();
            var address = parseResult.GetValue(addressArgument);
            var port = parseResult.GetValue(portOption);
            var wait = parseResult.GetValue(waitOption) || string.IsNullOrWhiteSpace(address);

            try
            {
                if (wait)
                {
                    // Код придумывает сопряжение и сообщает сразу: его надо продиктовать
                    // тому, кто стоит у агента, а не показать после того, как всё случилось.
                    var progress = new Progress<PairingProgress>(p => Console.WriteLine(p.Message));

                    var agent = await directory
                        .PairByWaitingAsync(port, progress, cancellationToken)
                        .ConfigureAwait(false);

                    AgentsRenderer.WritePaired(agent);

                    return 0;
                }

                var typed = parseResult.GetValue(codeOption);

                if (string.IsNullOrWhiteSpace(typed))
                {
                    Console.Error.WriteLine(
                        "Нужен код сопряжения. Получить его на машине агента: "
                        + "storm-agent listen --сопряжение");

                    return 2;
                }

                var paired = await directory
                    .PairByDialingAsync(address!, port, typed, cancellationToken)
                    .ConfigureAwait(false);

                AgentsRenderer.WritePaired(paired);

                return 0;
            }
            catch (Exception ex) when (ex is AgentException or InvalidOperationException or IOException)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.WriteLine("Сопряжение прервано.");

                return 1;
            }
        });

        return command;
    }

    private static Command BuildCheck(IServiceProvider services)
    {
        var nameArgument = new Argument<string>("агент")
        {
            Description = "Имя, псевдоним или начало отпечатка.",
        };

        var command = new Command("check", "Проверить связь, ничего не измеряя.") { nameArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var directory = services.GetRequiredService<IAgentDirectory>();

            try
            {
                var agent = await directory
                    .CheckAsync(parseResult.GetValue(nameArgument)!, cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine($"Связь есть: {agent.DisplayName} ({agent.Product}).");
                Console.WriteLine($"Отпечаток совпал — подтверждения не потребовалось.");

                return 0;
            }
            catch (Exception ex) when (ex is AgentException or InvalidOperationException or IOException)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }
        });

        return command;
    }

    private static Command BuildRename(IServiceProvider services)
    {
        var nameArgument = new Argument<string>("агент") { Description = "Кого переименовать." };
        var aliasArgument = new Argument<string>("имя") { Description = "Как называть в списке." };

        var command = new Command("name", "Дать агенту понятное имя.") { nameArgument, aliasArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var directory = services.GetRequiredService<IAgentDirectory>();

            try
            {
                var agent = await directory
                    .RenameAsync(parseResult.GetValue(nameArgument)!, parseResult.GetValue(aliasArgument)!, cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine($"Теперь это «{agent.DisplayName}» ({agent.MachineName}).");

                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }
        });

        return command;
    }

    private static Command BuildForget(IServiceProvider services)
    {
        var nameArgument = new Argument<string>("агент") { Description = "Кого забыть." };

        var command = new Command("forget", "Забыть сопряжение. Следующее соединение потребует нового кода.")
        {
            nameArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var directory = services.GetRequiredService<IAgentDirectory>();

            try
            {
                if (await directory.ForgetAsync(parseResult.GetValue(nameArgument)!, cancellationToken)
                        .ConfigureAwait(false))
                {
                    Console.WriteLine("Забыт. Агент об этом не знает — на его стороне сопряжение "
                                      + "надо снять отдельно: storm-agent peers forget <отпечаток>.");

                    return 0;
                }

                Console.Error.WriteLine("Такого агента среди сопряжённых нет.");

                return 1;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }
        });

        return command;
    }
}
