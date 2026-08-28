using System.CommandLine;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
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
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string ServiceName = "StormAgent";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Служба запускается диспетчером без консоли и без аргументов разбора:
        // ветка отделена до System.CommandLine, потому что SCM ждёт отклика
        // в считаные секунды и разбор командной строки здесь только помешает.
        if (args is ["--служба"] or ["--service"])
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

    private static Command BuildService()
    {
        var command = new Command("service", "Установка постоянной службой. Требует прав администратора.");

        var install = new Command("install", "Установить и запустить службу.");

        install.SetAction((_, _) =>
        {
            if (!IsElevated())
            {
                Console.Error.WriteLine(
                    "Установка службы требует прав администратора — это единственное место, "
                    + "где агенту они нужны. Для работы без прав используй listen или connect.");

                return Task.FromResult(2);
            }

            var path = Environment.ProcessPath;

            if (path is null)
            {
                Console.Error.WriteLine("Не удалось определить путь к собственному файлу.");

                return Task.FromResult(1);
            }

            var code = Sc($"create {ServiceName} binPath= \"\\\"{path}\\\" --служба\" start= auto "
                          + "DisplayName= \"Storm Machine — агент измерений\"");

            if (code != 0)
            {
                return Task.FromResult(code);
            }

            Sc($"description {ServiceName} \"Удалённая точка измерения Storm Machine.\"");

            return Task.FromResult(Sc($"start {ServiceName}"));
        });

        var uninstall = new Command("uninstall", "Остановить и удалить службу.");

        uninstall.SetAction((_, _) =>
        {
            if (!IsElevated())
            {
                Console.Error.WriteLine("Удаление службы требует прав администратора.");

                return Task.FromResult(2);
            }

            Sc($"stop {ServiceName}");

            return Task.FromResult(Sc($"delete {ServiceName}"));
        });

        command.Subcommands.Add(install);
        command.Subcommands.Add(uninstall);

        command.SetAction((_, _) =>
        {
            Console.WriteLine("storm-agent service install   — установить и запустить");
            Console.WriteLine("storm-agent service uninstall — остановить и удалить");
            Console.WriteLine();
            Console.WriteLine("Служба — единственный режим, требующий прав администратора.");
            Console.WriteLine("Для площадки, где прав нет, годятся listen и connect.");

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
        };
    }

    private static int Sc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null)
        {
            Console.Error.WriteLine("Не удалось запустить sc.exe.");

            return 1;
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        if (output.Trim().Length > 0)
        {
            Console.WriteLine(output.Trim());
        }

        return process.ExitCode;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// Оболочка службы.
/// </summary>
/// <remarks>
/// Диспетчер служб ждёт отклика в считаные секунды, поэтому запуск здесь только
/// заводит фоновую работу и сразу возвращается. Всё, что дольше, — уже в самом агенте.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class AgentService : ServiceBase
{
    private readonly CancellationTokenSource _stopping = new();

    private Task? _work;

    public AgentService() => ServiceName = "StormAgent";

    protected override void OnStart(string[] args)
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        var host = new AgentHost(new AgentSettings
        {
            IdentityPath = Path.Combine(directory, "storm-agent.identity.pfx"),
            PeerBookPath = Path.Combine(directory, "storm-agent.peers.json"),
            Port = SecureChannel.DefaultPort,

            // Служба работает без человека рядом, и новых собеседников не принимает:
            // код сопряжения некому прочитать. Сопрягать надо до установки, запустив
            // агента в консоли.
            Pairing = null,
            Log = message => EventLog.WriteEntry(message),
        });

        _work = host.ListenAsync(_stopping.Token);
    }

    protected override void OnStop()
    {
        _stopping.Cancel();

        try
        {
            _work?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Прерывание — штатный способ остановки, и жаловаться на него незачем.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}
