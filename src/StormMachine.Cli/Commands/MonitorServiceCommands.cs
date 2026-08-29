using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Установка планировщика мониторов постоянной службой.
/// </summary>
/// <remarks>
/// Закрывает долг И-14: до И-21 планировщик работал, только пока открыт клиент.
/// Закрыл окно — сроки проходят мимо и записываются пропусками. Записывается честно,
/// но это не наблюдение, а его отсутствие с протоколом; монитор при этом остаётся
/// обещанием непрерывности, которого продукт не держит.
/// <para>
/// Устройство повторяет службу агента (<c>storm-agent service</c>) — там та же задача
/// решена и решена верно. Два отличия существенны, и оба вытекают из того, что клиент,
/// в отличие от агента, работает <b>с данными оператора</b>, а не со своей папкой.
/// </para>
/// <para>
/// <b>Первое: база.</b> Путь к ней вычисляется из профиля пользователя, а у службы
/// профиль свой. Служба под <c>LocalSystem</c> открыла бы
/// <c>C:\Windows\System32\config\systemprofile\…</c>, не нашла бы там ни одного монитора
/// и честно доложила бы, что сторожить нечего, — навсегда. Поэтому путь к базе
/// вписывается в командную строку службы при установке, а не вычисляется ею заново.
/// Это ровно тот урок, что записан в И-13: путь не опознаёт файл, а профиль
/// не опознаёт пользователя.
/// </para>
/// <para>
/// <b>Второе: секреты.</b> Учётные данные SNMP и пароль почты зашифрованы DPAPI
/// в области <c>CurrentUser</c>. Другая учётная запись их не расшифрует — ключ
/// принадлежит пользователю, и это правильно. Значит служба, которой они нужны,
/// обязана работать под учётной записью оператора; под <c>LocalSystem</c> она сможет
/// пинговать и звать webhook, но не опрашивать оборудование и не слать письма.
/// Об этом сказано при установке, а не выясняется при первом отказе.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class MonitorServiceCommands
{
    public const string ServiceName = "StormMonitor";

    /// <summary>Скрытый ключ, которым диспетчер служб запускает этот же файл.</summary>
    /// <remarks>
    /// Разбирается до <c>System.CommandLine</c>: диспетчер ждёт отклика службы
    /// в считаные секунды, и разбор командной строки здесь только мешает. Так же
    /// сделано у агента.
    /// </remarks>
    public const string ServiceSwitch = "--служба-мониторов";

    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command(
            "service",
            "Наблюдать постоянно: планировщик службой Windows, а не открытым окном.");

        command.Subcommands.Add(BuildInstall(services));
        command.Subcommands.Add(BuildUninstall());
        command.Subcommands.Add(BuildStatus(services));

        command.SetAction((_, _) =>
        {
            Console.WriteLine("storm monitors service install   — установить и запустить");
            Console.WriteLine("storm monitors service status    — что со службой сейчас");
            Console.WriteLine("storm monitors service uninstall — остановить и удалить");
            Console.WriteLine();
            Console.WriteLine("Без службы планировщик работает, только пока открыт клиент:");
            Console.WriteLine("закрытое окно означает, что сроки пройдут мимо и запишутся");
            Console.WriteLine("пропусками. Пропуск — честная запись, но не наблюдение.");

            return Task.FromResult(0);
        });

        return command;
    }

    // ------------------------------------------------------------------ установка

    private static Command BuildInstall(IServiceProvider services)
    {
        var systemOption = new Option<bool>("--система", "--local-system")
        {
            Description = "Работать под LocalSystem, без пароля. SNMP и почта при этом откажут.",
        };

        var accountOption = new Option<string?>("--учётная-запись", "--account")
        {
            Description = "Под какой записью работать. По умолчанию — текущая.",
        };

        var command = new Command("install", "Установить и запустить службу. Требует прав администратора.")
        {
            systemOption,
            accountOption,
        };

        command.SetAction((parseResult, cancellationToken) => InstallAsync(
            services,
            parseResult.GetValue(systemOption),
            parseResult.GetValue(accountOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> InstallAsync(
        IServiceProvider services,
        bool asSystem,
        string? account,
        CancellationToken cancellationToken)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine(
                "Установка службы требует прав администратора. Запустите консоль от имени "
                + "администратора и повторите — это единственное место, где права нужны.");

            return 2;
        }

        if (Environment.ProcessPath is not { } executable)
        {
            Console.Error.WriteLine("Не удалось определить путь к собственному файлу.");

            return 1;
        }

        // База берётся у того хранилища, которым пользуется этот самый клиент.
        // Вычислять её заново внутри службы нельзя: у службы другой профиль.
        var store = services.GetRequiredService<IRunStore>();
        var database = store.Location;

        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var monitors = await services.GetRequiredService<IMonitorStore>()
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        var enabled = monitors.Count(m => m.IsEnabled);

        Console.WriteLine($"База      : {database}");
        Console.WriteLine($"Мониторов : {monitors.Count}, включённых {enabled}");
        Console.WriteLine();

        if (enabled == 0)
        {
            // Не отказ: службу можно поставить заранее, а мониторы завести потом.
            // Но промолчать нельзя — иначе оператор решит, что наблюдение пошло.
            Console.WriteLine("Включённых мониторов нет: служба встанет и будет ждать, пока они появятся.");
            Console.WriteLine();
        }

        var (identity, password) = ResolveAccount(asSystem, account);

        if (identity is null)
        {
            return 2;
        }

        var create = $"create {ServiceName} binPath= \"{BuildBinPath(executable, database)}\" start= auto "
                     + "DisplayName= \"Storm Machine — наблюдение по расписанию\"";

        if (!string.Equals(identity, "LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            create += $" obj= \"{identity}\" password= \"{password}\"";
        }

        var code = Sc(create);

        if (code != 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Служба не создана. Если она уже стоит — снимите её командой "
                + "«storm monitors service uninstall» и повторите.");

            return code;
        }

        Sc($"description {ServiceName} \"Выполняет проверки мониторов Storm Machine по расписанию, "
           + $"пока машина включена. База: {database}\"");

        // Перезапуск после сбоя: наблюдение, прекратившееся из-за одной ошибки
        // и не возобновившееся, — то же самое отсутствие наблюдения.
        Sc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");

        code = Sc($"start {ServiceName}");

        if (code != 0)
        {
            return code;
        }

        Console.WriteLine();
        Console.WriteLine("Служба установлена и запущена. Проверки идут, пока включена машина, —");
        Console.WriteLine("клиент для этого держать открытым больше не нужно.");
        Console.WriteLine();
        Console.WriteLine("  storm monitors service status   что со службой сейчас");
        Console.WriteLine("  storm monitors checks <имя>     что она уже проверила");

        return 0;
    }

    /// <summary>
    /// Собирает то, что диспетчер служб будет запускать.
    /// </summary>
    /// <remarks>
    /// Вынесено отдельно и покрыто проверками из-за кавычек. Продукт ставится
    /// в «C:\Program Files\…», база лежит в профиле пользователя, и оба пути содержат
    /// пробелы. Строка запуска службы разбирается дважды — сначала <c>sc.exe</c>,
    /// потом самим диспетчером, — и кавычки нужны на обоих уровнях: внешние
    /// добавляет вызывающий, внутренние стоят здесь.
    /// <para>
    /// Ошибка здесь не выглядит ошибкой: <c>sc</c> создаёт службу успешно, а падает
    /// она при запуске — с сообщением о ненайденном файле, в котором путь обрезан
    /// по первому пробелу. Проверить это глазами один раз мало: строка перестраивается
    /// при каждой правке.
    /// </para>
    /// </remarks>
    internal static string BuildBinPath(string executable, string database) =>
        $"\\\"{executable}\\\" {ServiceSwitch} --база \\\"{database}\\\"";

    /// <summary>
    /// Под какой учётной записью работать и с каким паролем.
    /// </summary>
    /// <remarks>
    /// Пароль спрашивается с клавиатуры и в командную строку не попадает: ключ
    /// командной строки остался бы в истории оболочки, в списке процессов и в логах
    /// терминала — трёх местах, откуда его никто потом не вычистит. Тот же порядок,
    /// что у паролей SNMP и почты.
    /// </remarks>
    private static (string? Identity, string? Password) ResolveAccount(bool asSystem, string? account)
    {
        if (asSystem)
        {
            Console.WriteLine("Учётная запись: LocalSystem (пароль не нужен).");
            Console.WriteLine();
            Console.WriteLine("ВНИМАНИЕ: под этой записью откажут мониторы, которым нужны секреты.");
            Console.WriteLine("Учётные данные SNMP и пароль почты зашифрованы ключом вашей учётной");
            Console.WriteLine("записи, и другая их не расшифрует — так устроен DPAPI, и это правильно.");
            Console.WriteLine("Останутся доступны пробы без секретов и оповещение через webhook.");
            Console.WriteLine();

            return ("LocalSystem", null);
        }

        var identity = string.IsNullOrWhiteSpace(account)
            ? WindowsIdentity.GetCurrent().Name
            : account.Trim();

        Console.WriteLine($"Учётная запись: {identity}");
        Console.WriteLine();
        Console.WriteLine("Под ней служба видит вашу базу и ваши секреты — SNMP и почта будут работать.");
        Console.WriteLine("Windows требует для этого пароль записи. Он передаётся диспетчеру служб");
        Console.WriteLine("и в командной строке не сохраняется.");
        Console.WriteLine();

        var password = Secrets.Read($"Пароль записи {identity}");

        if (password is null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Без пароля служба под вашей записью не ставится. Можно поставить её "
                + "под LocalSystem ключом --система, но тогда SNMP и почта откажут.");

            return (null, null);
        }

        return (identity, password);
    }

    // -------------------------------------------------------------------- снятие

    private static Command BuildUninstall()
    {
        var command = new Command("uninstall", "Остановить и удалить службу. Требует прав администратора.");

        command.SetAction((_, _) =>
        {
            if (!IsElevated())
            {
                Console.Error.WriteLine("Удаление службы требует прав администратора.");

                return Task.FromResult(2);
            }

            Sc($"stop {ServiceName}");

            var code = Sc($"delete {ServiceName}");

            if (code == 0)
            {
                Console.WriteLine();
                Console.WriteLine("Служба снята. Мониторы и их история остались в базе:");
                Console.WriteLine("наблюдение прекратилось, измеренное — нет.");
            }

            return Task.FromResult(code);
        });

        return command;
    }

    // ------------------------------------------------------------------ состояние

    private static Command BuildStatus(IServiceProvider services)
    {
        var command = new Command("status", "Стоит ли служба, работает ли и за какой базой смотрит.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var found = Find();

            if (found is null)
            {
                Console.WriteLine("Служба не установлена.");
                Console.WriteLine();
                Console.WriteLine("Пока её нет, проверки идут только при открытом клиенте:");
                Console.WriteLine("«storm monitors watch» или запущенное окно. Поставить постоянно —");
                Console.WriteLine("«storm monitors service install» от имени администратора.");

                return 0;
            }

            using var service = found;

            Console.WriteLine($"Служба    : {service.ServiceName} — {Describe(service.Status)}");

            var store = services.GetRequiredService<IRunStore>();

            Console.WriteLine($"База этого клиента: {store.Location}");
            Console.WriteLine();
            Console.WriteLine("Сверьте её с той, что вписана в службу:");
            Console.WriteLine($"  sc qc {ServiceName}");
            Console.WriteLine();
            Console.WriteLine("Разные базы — самая частая причина «служба работает, а проверок нет»:");
            Console.WriteLine("у службы другой профиль, и путь по умолчанию у неё тоже другой.");

            if (service.Status != ServiceControllerStatus.Running)
            {
                return 0;
            }

            var monitors = await services.GetRequiredService<IMonitorStore>()
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);

            var enabled = monitors.Count(m => m.IsEnabled);

            Console.WriteLine();
            Console.WriteLine($"Включённых мониторов в этой базе: {enabled.ToString(CultureInfo.InvariantCulture)}");

            return 0;
        });

        return command;
    }

    private static ServiceController? Find()
    {
        try
        {
            var controller = new ServiceController(ServiceName);

            // Обращение к Status бросает, если службы нет: сам конструктор не проверяет.
            _ = controller.Status;

            return controller;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string Describe(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => "работает",
        ServiceControllerStatus.Stopped => "остановлена",
        ServiceControllerStatus.StartPending => "запускается",
        ServiceControllerStatus.StopPending => "останавливается",
        ServiceControllerStatus.Paused => "приостановлена",
        _ => status.ToString(),
    };

    // ------------------------------------------------------------------ помощники

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Вызывает <c>sc.exe</c>.
    /// </summary>
    /// <remarks>
    /// Через <c>sc</c>, а не через API управления службами: так оператор видит ту же
    /// команду, которую набрал бы сам, и может повторить её руками. Тот же приём
    /// у службы агента.
    /// </remarks>
    private static int Sc(string arguments)
    {
        var info = new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
        };

        using var process = Process.Start(info);

        if (process is null)
        {
            Console.Error.WriteLine("Не удалось запустить sc.exe.");

            return 1;
        }

        process.WaitForExit();

        return process.ExitCode;
    }
}
