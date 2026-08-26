using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Строит команду командной строки по паспорту пробы.
/// </summary>
/// <remarks>
/// Реализация принципа 1 из docs/01-analysis.md §8.2: проба объявляет параметры, а
/// интерфейс строит форму по объявлению. Здесь «форма» — набор ключей командной строки.
/// <para>
/// Появилось в И-2. Шесть проб потребовали бы шести почти одинаковых файлов команд;
/// вместо этого команда одна и выводится из объявления. Проверка того же принципа
/// в графическом клиенте будет ровно такой же по устройству.
/// </para>
/// </remarks>
internal static class ProbeCommandFactory
{
    public static Command Create(IServiceProvider services, IProbe probe)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(probe);

        var descriptor = probe.Descriptor;

        var targetArgument = new Argument<string>("цель")
        {
            Description = TargetHint(descriptor),
        };

        var readers = new List<(string Name, Func<ParseResult, object?> Read)>();
        var command = new Command(descriptor.Name, descriptor.Description)
        {
            targetArgument,
        };

        foreach (var parameter in descriptor.Parameters)
        {
            var (option, reader) = CreateOption(parameter);
            command.Options.Add(option);
            readers.Add((parameter.Name, reader));
        }

        var quietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Только итоговая сводка, без построчного вывода.",
        };

        command.Options.Add(quietOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, read) in readers)
            {
                parameters[name] = read(parseResult);
            }

            var request = new ProbeRequest
            {
                Target = ParseTarget(parseResult.GetValue(targetArgument)!),
                Parameters = parameters,
            };

            return await RunAsync(services, probe, request, parseResult.GetValue(quietOption), cancellationToken)
                .ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        IServiceProvider services,
        IProbe probe,
        ProbeRequest request,
        bool quiet,
        CancellationToken cancellationToken)
    {
        var environment = services.GetRequiredService<INetworkEnvironment>();
        var clock = services.GetRequiredService<IHighResolutionClock>();
        var descriptor = probe.Descriptor;

        var errors = probe.Validate(request);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"Параметр --{error.ParameterName}: {error.Message}");
            }

            return 2;
        }

        await clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);

        var adapter = environment.GetPrimaryAdapter();
        var context = BuildContext(adapter, clock, descriptor.Methodology);
        var collector = new ProbeCollector();

        ProbeRenderer.WriteHeader(descriptor, request.Target, context, adapter);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interrupted = false;

        void OnCancelKey(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            interrupted = true;
            linked.Cancel();
        }

        Console.CancelKeyPress += OnCancelKey;

        var samples = new List<Sample>();

        try
        {
            await foreach (var sample in probe.ExecuteAsync(request, collector, linked.Token).ConfigureAwait(false))
            {
                samples.Add(sample);

                if (!quiet)
                {
                    ProbeRenderer.WriteLiveSample(descriptor, sample);
                }
            }
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка выполнения: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKey;
        }

        var result = new ProbeResult
        {
            Id = Guid.NewGuid(),
            Kind = descriptor.Kind,
            Target = request.Target,
            ResolvedAddress = collector.ResolvedAddress,
            Context = context,
            Unit = descriptor.Unit,
            Samples = samples,
            Facts = collector.Facts,
            CompletedUtc = DateTimeOffset.UtcNow,
            WasCancelled = interrupted,
        };

        ProbeRenderer.WriteSummary(descriptor, result, clock);

        return result.SuccessCount > 0 ? 0 : 1;
    }

    private static (Option Option, Func<ParseResult, object?> Read) CreateOption(ProbeParameter parameter)
    {
        var name = "--" + parameter.Name;
        var description = parameter.Description ?? parameter.Label;

        switch (parameter.Type)
        {
            case ProbeParameterType.Boolean:
            {
                var option = new Option<bool>(name)
                {
                    Description = description,
                    DefaultValueFactory = _ => parameter.DefaultValue is bool b && b,
                };

                return (option, parse => parse.GetValue(option));
            }

            case ProbeParameterType.Text:
            case ProbeParameterType.Choice:
            {
                var fallback = parameter.DefaultValue as string ?? string.Empty;
                var option = new Option<string>(name)
                {
                    Description = description,
                    DefaultValueFactory = _ => fallback,
                };

                return (option, parse => parse.GetValue(option));
            }

            case ProbeParameterType.Decimal:
            {
                var fallback = Convert.ToDouble(parameter.DefaultValue ?? 0d, System.Globalization.CultureInfo.InvariantCulture);
                var option = new Option<double>(name)
                {
                    Description = description,
                    DefaultValueFactory = _ => fallback,
                };

                return (option, parse => parse.GetValue(option));
            }

            default:
            {
                var fallback = Convert.ToInt32(parameter.DefaultValue ?? 0, System.Globalization.CultureInfo.InvariantCulture);
                var option = new Option<int>(name)
                {
                    Description = description,
                    DefaultValueFactory = _ => fallback,
                };

                return (option, parse => parse.GetValue(option));
            }
        }
    }

    private static string TargetHint(ProbeDescriptor descriptor) => descriptor.Kind switch
    {
        ProbeKind.Dns => "Имя для разрешения, например example.com.",
        ProbeKind.Http => "Адрес: example.com или https://example.com/path.",
        ProbeKind.Tls => "Имя узла, например example.com.",
        _ => "IP-адрес, имя узла или слово gateway для шлюза по умолчанию.",
    };

    private static Target ParseTarget(string raw) =>
        raw.Equals("gateway", StringComparison.OrdinalIgnoreCase) || raw.Equals("шлюз", StringComparison.OrdinalIgnoreCase)
            ? Target.Gateway("шлюз по умолчанию")
            : Target.Parse(raw);

    private static MeasurementContext BuildContext(
        NetworkAdapter? adapter,
        IHighResolutionClock clock,
        Methodology methodology) => new()
        {
            InterfaceName = adapter?.Name ?? "неизвестен",
            AdapterKind = adapter?.Kind ?? AdapterKind.Unknown,
            InterfaceAddress = adapter?.IPv4Address,
            CalibrationBaselineMs = clock.CalibrationBaselineMs,
            ProductVersion = ProductInfo.Version,
            Methodology = methodology,
            StartedUtc = DateTimeOffset.UtcNow,
        };
}
