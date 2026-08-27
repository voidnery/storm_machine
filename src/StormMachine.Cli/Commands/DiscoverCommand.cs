using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Discovery;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Команды обнаружения и инвентаря: <c>storm discover</c>, <c>storm devices</c>.
/// </summary>
/// <remarks>
/// Сканирование — активное действие по чужой сети. Отсюда три обязанности, которых нет
/// у остальных команд: показать объём <b>до</b> запуска, ограничить темп и записать
/// сделанное в журнал аудита. Требование раздела «Этика» в README.
/// </remarks>
internal static class DiscoverCommand
{
    public static Command CreateDiscover(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var rangeArgument = new Argument<string>("диапазон")
        {
            Description = "192.168.1.0/24, 192.168.1.10-192.168.1.40 или слово auto — подсеть текущего интерфейса.",
            DefaultValueFactory = _ => "auto",
        };

        var parallelismOption = new Option<int>("--parallel")
        {
            Description = "Сколько адресов опрашивать одновременно.",
            DefaultValueFactory = _ => 64,
        };

        var timeoutOption = new Option<int>("--timeout")
        {
            Description = "Сколько ждать ответа от адреса, мс.",
            DefaultValueFactory = _ => 700,
        };

        var noPortsOption = new Option<bool>("--no-ports")
        {
            Description = "Не проверять частые порты у молчащих узлов.",
        };

        var noNamesOption = new Option<bool>("--no-names")
        {
            Description = "Не выяснять имена узлов.",
        };

        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Не спрашивать подтверждения диапазона.",
        };

        var saveOption = new Option<bool>("--save")
        {
            Description = "Сохранить результат в инвентарь (storm devices).",
            DefaultValueFactory = _ => true,
        };

        var command = new Command("discover", "Сканирование сети: какие узлы в ней есть.")
        {
            rangeArgument,
            parallelismOption,
            timeoutOption,
            noPortsOption,
            noNamesOption,
            yesOption,
            saveOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => await RunAsync(
            services,
            parseResult.GetValue(rangeArgument)!,
            new DiscoveryRequest
            {
                Range = AddressRange.Parse("0.0.0.0"),
                Parallelism = parseResult.GetValue(parallelismOption),
                TimeoutMs = parseResult.GetValue(timeoutOption),
                ProbeCommonPorts = !parseResult.GetValue(noPortsOption),
                ResolveNames = !parseResult.GetValue(noNamesOption),
            },
            parseResult.GetValue(yesOption),
            parseResult.GetValue(saveOption),
            cancellationToken).ConfigureAwait(false));

        return command;
    }

    private static async Task<int> RunAsync(
        IServiceProvider services,
        string rangeText,
        DiscoveryRequest template,
        bool assumeYes,
        bool save,
        CancellationToken cancellationToken)
    {
        var environment = services.GetRequiredService<INetworkEnvironment>();
        var discovery = services.GetRequiredService<IDiscoveryService>();
        var store = services.GetRequiredService<IDeviceStore>();
        var oui = services.GetRequiredService<IOuiCatalog>();

        AddressRange range;

        try
        {
            range = Resolve(rangeText, environment);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var adapter = environment.GetPrimaryAdapter();
        var request = template with { Range = range };

        DeviceRenderer.WriteScanHeader(range, adapter, request, oui);

        if (!assumeYes && !Confirm(range))
        {
            Console.WriteLine("Отменено.");
            return 1;
        }

        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await store.RecordAsync(
            new AuditEntry
            {
                Id = Guid.NewGuid(),
                AtUtc = DateTimeOffset.UtcNow,
                Action = "discovery",
                Target = range.Text,
                Operator = Environment.UserName,
                Details = $"интерфейс {adapter?.Name ?? "неизвестен"}, адресов {range.Count.ToString(CultureInfo.InvariantCulture)}, "
                          + $"одновременно {request.Parallelism.ToString(CultureInfo.InvariantCulture)}",
            },
            cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnCancelKey(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            linked.Cancel();
        }

        Console.CancelKeyPress += OnCancelKey;

        DiscoveryScan scan;

        try
        {
            scan = await discovery
                .ScanAsync(request, DeviceRenderer.CreateProgressWriter(), linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Сканирование не выполнено: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKey;
        }

        DeviceRenderer.WriteScan(scan);

        if (save)
        {
            await store.SaveScanAsync(scan, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Сохранено в инвентарь: {scan.Id}");
            Console.WriteLine("  storm devices");
        }

        return scan.Responded > 0 ? 0 : 1;
    }

    /// <summary>
    /// Подсеть текущего интерфейса, если диапазон не задан явно.
    /// </summary>
    /// <remarks>
    /// «Своя сеть» — единственное разумное значение по умолчанию: инструмент открывают,
    /// чтобы увидеть сеть, в которой стоит компьютер, а не чтобы вводить маску.
    /// </remarks>
    private static AddressRange Resolve(string text, INetworkEnvironment environment)
    {
        if (!text.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !text.Equals("своя", StringComparison.OrdinalIgnoreCase))
        {
            return AddressRange.Parse(text);
        }

        var adapter = environment.GetPrimaryAdapter();

        if (adapter?.IPv4Address is not { } address || adapter.PrefixLength <= 0)
        {
            throw new FormatException(
                "Не удалось определить свою подсеть — у основного интерфейса нет адреса IPv4. "
                + "Укажите диапазон явно, например 192.168.1.0/24.");
        }

        return AddressRange.FromInterface(System.Net.IPAddress.Parse(address), adapter.PrefixLength);
    }

    private static bool Confirm(AddressRange range)
    {
        Console.WriteLine();
        Console.Write($"Опросить {range.Count.ToString(CultureInfo.InvariantCulture)} адресов? [y/N] ");

        var answer = Console.ReadLine();

        return answer is not null
               && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                   || answer.Trim().Equals("д", StringComparison.OrdinalIgnoreCase));
    }
}
