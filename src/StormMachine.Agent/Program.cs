using System.CommandLine;
using System.ServiceProcess;
using StormMachine.Protocol;

namespace StormMachine.Agent;

/// <summary>
/// Точка входа агента.
/// </summary>
/// <remarks>
/// Три режима запуска — не три программы, а три способа завести один и тот же
/// <see cref="AgentHost"/>:
/// <list type="number">
/// <item><b>Портативный</b> — консоль на переднем плане. Ничего не устанавливает,
/// прав не требует, живёт в папке, откуда запущен. Это основной режим для площадки,
/// где ничего ставить не дадут.</item>
/// <item><b>Дозвон</b> — соединяется сам и работает, пока клиент не отключится.
/// Существует потому, что входящие на Windows заблокированы по умолчанию, а правило
/// требует администратора: исходящие разрешены, и в этом режиме прав не нужно вовсе.</item>
/// <item><b>Служба</b> — постоянная точка на своей машине. Установка требует прав,
/// и это единственное место, где они нужны.</item>
/// </list>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Служба запускается диспетчером без консоли и без аргументов разбора:
        // ветка отделена до System.CommandLine, потому что SCM ждёт отклика
        // в считаные секунды и разбор командной строки здесь только помешает.
        //
        // Ключ существует только на Windows: на Linux постоянную точку поднимает
        // systemd обычной командой listen, и своей ветки запуска ей не нужно.
        if (args is ["--служба"] or ["--service"] && OperatingSystem.IsWindows())
        {
            ServiceBase.Run(new AgentService());

            return 0;
        }

        var root = new RootCommand(
            "storm-agent — удалённая точка измерения Storm Machine. "
            + "Портативен: ничего не устанавливает и не требует прав администратора.");

        root.Subcommands.Add(BuildListen());
        root.Subcommands.Add(BuildConnect());
        root.Subcommands.Add(BuildPeers());
        root.Subcommands.Add(BuildService());

        root.SetAction((_, cancellationToken) => Listen(SecureChannel.DefaultPort, null, cancellationToken));

        var parsing = new ParserConfiguration { ResponseFileTokenReplacer = null };

        return root.Parse(args, parsing).Invoke();
    }

    private static Command BuildListen()
    {
        var portOption = new Option<int>("--порт", "--port")
        {
            Description = "Порт управляющего канала.",
            DefaultValueFactory = _ => SecureChannel.DefaultPort,
        };

        var pairOption = new Option<bool>("--сопряжение", "--pair")
        {
            Description = "Выдать код сопряжения и принять нового собеседника.",
        };

        var command = new Command("listen", "Ждать подключения клиента. Требует разрешённых входящих.")
        {
            portOption,
            pairOption,
        };

        command.SetAction((parseResult, cancellationToken) => Listen(
            parseResult.GetValue(portOption),
            parseResult.GetValue(pairOption) ? PairingOffer.Issue() : null,
            cancellationToken));

        return command;
    }

    private static Command BuildConnect()
    {
        var addressArgument = new Argument<string>("адрес")
        {
            Description = "Куда дозваниваться: адрес машины с клиентом.",
        };

        var portOption = new Option<int>("--порт", "--port")
        {
            Description = "Порт клиента.",
            DefaultValueFactory = _ => SecureChannel.DefaultPort,
        };

        var codeOption = new Option<string?>("--код", "--code")
        {
            Description = "Код сопряжения, выданный клиентом. Нужен только при первом соединении.",
        };

        var command = new Command(
            "connect",
            "Дозвониться до клиента самому. Прав не требует: исходящие разрешены по умолчанию.")
        {
            addressArgument,
            portOption,
            codeOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var typed = parseResult.GetValue(codeOption);

            var host = new AgentHost(Settings(
                parseResult.GetValue(portOption),
                typed is { Length: > 0 } ? PairingOffer.For(typed, PairingCode.Lifetime) : null));

            try
            {
                await host.ConnectAsync(
                    parseResult.GetValue(addressArgument)!,
                    parseResult.GetValue(portOption),
                    cancellationToken).ConfigureAwait(false);

                return 0;
            }
            catch (ProtocolException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }
        });

        return command;
    }

    private static Command BuildPeers()
    {
        var command = new Command("peers", "Кого агент помнит.");

        var forget = new Argument<string?>("отпечаток")
        {
            Description = "Забыть собеседника с этим отпечатком.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var forgetCommand = new Command("forget", "Забыть сопряжение.") { forget };

        forgetCommand.SetAction((parseResult, _) =>
        {
            var book = new AgentHost(Settings(SecureChannel.DefaultPort, null)).Book;
            var thumbprint = (parseResult.GetValue(forget) ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);

            if (book.Forget(thumbprint))
            {
                Console.WriteLine("Забыт. Следующее соединение потребует нового сопряжения.");

                return Task.FromResult(0);
            }

            Console.Error.WriteLine("Такого отпечатка в списке нет.");

            return Task.FromResult(1);
        });

        command.Subcommands.Add(forgetCommand);

        command.SetAction((_, _) =>
        {
            var host = new AgentHost(Settings(SecureChannel.DefaultPort, null));

            Console.WriteLine($"Свой отпечаток: {host.Identity.ThumbprintForHumans}");
            Console.WriteLine();

            var peers = host.Book.All;

            if (peers.Count == 0)
            {
                Console.WriteLine("Сопряжений нет. Запусти listen --сопряжение или connect --код.");

                return Task.FromResult(0);
            }

            Console.WriteLine($"  {"машина",-24} {"продукт",-24} сопряжён");

            foreach (var peer in peers)
            {
                Console.WriteLine($"  {peer.MachineName,-24} {peer.Product,-24} "
                                  + $"{peer.PairedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
                Console.WriteLine($"  {string.Empty,-24} {PeerIdentity.Group(peer.Thumbprint)}");
            }

            return Task.FromResult(0);
        });

        return command;
    }

    /// <summary>
    /// Постоянная точка на этой машине.
    /// </summary>
    /// <remarks>
    /// Команда одна, способ разный: на Windows — диспетчер служб, на Linux — systemd.
    /// Заставлять человека знать, на какой он машине, незачем: продукт и так знает.
    /// На macOS постоянной точки нет, и об этом говорится прямо, а не отказом без причины.
    /// </remarks>
    private static Command BuildService()
    {
        var command = new Command("service", "Постоянная точка на этой машине: служба Windows или юнит systemd.");

        var install = new Command("install", "Установить и запустить.");

        install.SetAction((_, _) =>
        {
            var path = Environment.ProcessPath;

            if (path is null)
            {
                Console.Error.WriteLine("Не удалось определить путь к собственному файлу.");

                return Task.FromResult(1);
            }

            if (OperatingSystem.IsWindows())
            {
                if (!Environment.IsPrivilegedProcess)
                {
                    Console.Error.WriteLine(
                        "Установка службы требует прав администратора — это единственное место, "
                        + "где агенту они нужны. Для работы без прав используй listen или connect.");

                    return Task.FromResult(2);
                }

                return Task.FromResult(WindowsServiceControl.Install(path));
            }

            if (OperatingSystem.IsLinux())
            {
                // Прав здесь не требуем: без root юнит ставится пользовательским,
                // и агент работает. Разница названа внутри — она в том, переживёт ли
                // служба выход из сеанса, а не в том, заработает ли вообще.
                return Task.FromResult(SystemdService.Install(path));
            }

            Console.Error.WriteLine(
                "Постоянная точка на этой платформе не заводится. Агент работает "
                + "командами listen и connect — для площадки этого достаточно.");

            return Task.FromResult(2);
        });

        var uninstall = new Command("uninstall", "Остановить и удалить.");

        uninstall.SetAction((_, _) =>
        {
            if (OperatingSystem.IsWindows())
            {
                if (!Environment.IsPrivilegedProcess)
                {
                    Console.Error.WriteLine("Удаление службы требует прав администратора.");

                    return Task.FromResult(2);
                }

                return Task.FromResult(WindowsServiceControl.Uninstall());
            }

            if (OperatingSystem.IsLinux())
            {
                return Task.FromResult(SystemdService.Uninstall());
            }

            Console.Error.WriteLine("Постоянная точка на этой платформе не заводится — удалять нечего.");

            return Task.FromResult(2);
        });

        command.Subcommands.Add(install);
        command.Subcommands.Add(uninstall);

        command.SetAction((_, _) =>
        {
            Console.WriteLine("storm-agent service install   — установить и запустить");
            Console.WriteLine("storm-agent service uninstall — остановить и удалить");
            Console.WriteLine();

            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("На Windows это служба, и это единственный режим, требующий прав администратора.");
            }
            else if (OperatingSystem.IsLinux())
            {
                Console.WriteLine("На Linux это юнит systemd. От root он системный и поднимается при загрузке;");
                Console.WriteLine("без root — пользовательский и живёт, пока открыт сеанс.");
            }
            else
            {
                Console.WriteLine("На этой платформе постоянная точка не заводится.");
            }

            Console.WriteLine("Для площадки, где ничего ставить не дадут, годятся listen и connect.");

            return Task.FromResult(0);
        });

        return command;
    }

    private static async Task<int> Listen(int port, PairingOffer? offer, CancellationToken cancellationToken)
    {
        var host = new AgentHost(Settings(port, offer));

        try
        {
            await host.ListenAsync(cancellationToken).ConfigureAwait(false);

            return 0;
        }
        catch (ProtocolException ex)
        {
            Console.Error.WriteLine(ex.Message);

            return 1;
        }
    }

    /// <summary>
    /// Где агент хранит своё.
    /// </summary>
    /// <remarks>
    /// Рядом с исполняемым файлом — в этом и состоит портативность: папку с агентом
    /// можно скопировать на флешку вместе с сопряжениями, и на другой машине он останется
    /// тем же собеседником. Запись в профиль пользователя такую переносимость сломала бы.
    /// </remarks>
    private static AgentSettings Settings(int port, PairingOffer? offer)
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath)
                        ?? AppContext.BaseDirectory;

        return new AgentSettings
        {
            IdentityPath = Path.Combine(directory, "storm-agent.identity.pfx"),
            PeerBookPath = Path.Combine(directory, "storm-agent.peers.json"),
            Port = port,
            Pairing = offer,

            // Запуск из консоли: человек рядом и код сопряжения ему видно.
            // У службы своя сборка настроек, и там этого признака нет.
            Interactive = true,
        };
    }

}

