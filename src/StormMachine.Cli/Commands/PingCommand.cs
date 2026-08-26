using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Cli.Commands;

/// <summary>
/// <c>storm ping</c> — доступность и задержка.
/// </summary>
/// <remarks>
/// Первая команда, которая действительно что-то измеряет. Показывает не только цифры,
/// но и условия измерения: через какой адаптер и с каким порогом разрешения — без этого
/// результаты несопоставимы между запусками.
/// </remarks>
internal static class PingCommand
{
    public static Command Create(IServiceProvider services)
    {
        var targetArgument = new Argument<string>("цель")
        {
            Description = "IP-адрес, имя узла или слово gateway для шлюза по умолчанию.",
        };

        var countOption = new Option<int>("--count", "-n") { Description = "Число проб.", DefaultValueFactory = _ => 4 };
        var intervalOption = new Option<int>("--interval", "-i") { Description = "Интервал между пробами, мс.", DefaultValueFactory = _ => 1000 };
        var sizeOption = new Option<int>("--size", "-s") { Description = "Размер полезной нагрузки, байт.", DefaultValueFactory = _ => 32 };
        var timeoutOption = new Option<int>("--timeout", "-w") { Description = "Таймаут ожидания ответа, мс.", DefaultValueFactory = _ => 2000 };
        var ttlOption = new Option<int>("--ttl") { Description = "Предельное число хопов.", DefaultValueFactory = _ => 128 };
        var dfOption = new Option<bool>("--df") { Description = "Запретить фрагментацию (флаг DF)." };
        var quietOption = new Option<bool>("--quiet", "-q") { Description = "Только итоговая сводка, без построчного вывода." };

        var command = new Command("ping", "Доступность и задержка: RTT, потери, джиттер RFC 3550, PDV.")
        {
            targetArgument, countOption, intervalOption, sizeOption,
            timeoutOption, ttlOption, dfOption, quietOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var rawTarget = parseResult.GetValue(targetArgument)!;
            var quiet = parseResult.GetValue(quietOption);

            var request = new ProbeRequest
            {
                Target = ParseTarget(rawTarget),
                Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [IcmpProbeParameters.Count] = parseResult.GetValue(countOption),
                    [IcmpProbeParameters.Interval] = parseResult.GetValue(intervalOption),
                    [IcmpProbeParameters.Size] = parseResult.GetValue(sizeOption),
                    [IcmpProbeParameters.Timeout] = parseResult.GetValue(timeoutOption),
                    [IcmpProbeParameters.Ttl] = parseResult.GetValue(ttlOption),
                    [IcmpProbeParameters.DontFragment] = parseResult.GetValue(dfOption),
                },
            };

            return await RunAsync(services, request, quiet, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        IServiceProvider services,
        ProbeRequest request,
        bool quiet,
        CancellationToken cancellationToken)
    {
        var registry = services.GetRequiredService<IProbeRegistry>();
        var environment = services.GetRequiredService<INetworkEnvironment>();
        var clock = services.GetRequiredService<IHighResolutionClock>();

        if (!registry.TryGet("ping", out var probe))
        {
            Console.Error.WriteLine("Проба ping не зарегистрирована.");
            return 1;
        }

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
        var context = BuildContext(adapter, clock, probe.Descriptor.Methodology);

        PrintHeader(request, context, adapter);

        // Прерывание по Ctrl+C не должно терять измеренное: отменяем прогон,
        // но обязательно печатаем сводку по тому, что успели собрать.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interrupted = false;

        void OnCancelKey(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            interrupted = true;
            linked.Cancel();
        }

        Console.CancelKeyPress += OnCancelKey;

        var samples = new List<Sample>(request.GetParameter(IcmpProbeParameters.Count, 4));

        try
        {
            await foreach (var sample in probe.ExecuteAsync(request, linked.Token).ConfigureAwait(false))
            {
                samples.Add(sample);

                if (!quiet)
                {
                    PrintSample(sample);
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
            Kind = ProbeKind.Icmp,
            Target = request.Target,
            Context = context,
            Unit = MeasurementUnit.Milliseconds,
            Samples = samples,
            CompletedUtc = DateTimeOffset.UtcNow,
            WasCancelled = interrupted,
        };

        PrintSummary(result, clock);

        return result.SuccessCount > 0 ? 0 : 1;
    }

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

    private static void PrintHeader(ProbeRequest request, MeasurementContext context, NetworkAdapter? adapter)
    {
        Console.WriteLine($"Цель      : {request.Target.DisplayName}");
        Console.WriteLine($"Интерфейс : {context.InterfaceName} ({EnvCommand.Describe(context.AdapterKind)})"
                          + (adapter?.IPv4Address is { } ip ? $", {ip}" : string.Empty));
        Console.WriteLine($"Методика  : {context.Methodology}");
        Console.WriteLine($"Порог     : {context.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс — ниже него измерения недостоверны");

        if (context.TimingWarning is { } warning)
        {
            Console.WriteLine();
            Console.WriteLine($"ВНИМАНИЕ: {warning}");
        }

        Console.WriteLine();
    }

    private static void PrintSample(Sample sample)
    {
        if (sample.IsSuccess)
        {
            var ttl = sample.Ttl is { } t ? $"  TTL={t}" : string.Empty;
            Console.WriteLine($"  {sample.Sequence,5}  {sample.Value.ToString("0.000", CultureInfo.InvariantCulture),9} мс{ttl}");
            return;
        }

        var reason = sample.Status switch
        {
            SampleStatus.Timeout => "таймаут",
            SampleStatus.Unreachable => "недоступен",
            SampleStatus.TtlExpired => $"истёк TTL на {sample.RespondedBy}",
            SampleStatus.Rejected => "пакет слишком велик (DF)",
            _ => "ошибка",
        };

        Console.WriteLine($"  {sample.Sequence,5}  {reason,12}");
    }

    private static void PrintSummary(ProbeResult result, IHighResolutionClock clock)
    {
        var stats = LatencyStatistics.Compute(result.Samples);

        Console.WriteLine();
        Console.WriteLine($"--- {result.Target.DisplayName} ---");

        if (result.WasCancelled)
        {
            Console.WriteLine("Прогон прерван. Ниже — то, что успели измерить.");
        }

        Console.WriteLine($"Отправлено {result.SentCount}, получено {result.SuccessCount}, "
                          + $"потеряно {result.LostCount} ({result.LossPercent.ToString("0.0", CultureInfo.InvariantCulture)}%)");

        if (stats.SampleCount == 0)
        {
            Console.WriteLine("Успешных ответов нет — статистику посчитать не по чему.");
            return;
        }

        static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

        Console.WriteLine();
        Console.WriteLine($"  RTT        min {F(stats.MinMs)}   avg {F(stats.MeanMs)}   max {F(stats.MaxMs)} мс");
        Console.WriteLine($"  Перцентили p50 {F(stats.P50Ms)}   p95 {F(stats.P95Ms)}   p99 {F(stats.P99Ms)} мс");
        Console.WriteLine($"  Разброс    stddev {F(stats.StdDevMs)} мс");
        Console.WriteLine($"  Джиттер    {F(stats.JitterRfc3550Ms)} мс   (RFC 3550 §6.4.1)");
        Console.WriteLine($"  PDV        {F(stats.PdvMs)} мс   (p99 − p50)");

        if (stats.P50Ms <= clock.CalibrationBaselineMs && clock.CalibrationBaselineMs > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Замечание: медиана на уровне порога разрешения измерительного стека —");
            Console.WriteLine("  различить сеть и собственные накладные расходы на таких значениях нельзя.");
        }
    }
}

/// <summary>Имена параметров пробы ICMP, чтобы не дублировать строки в командной строке.</summary>
internal static class IcmpProbeParameters
{
    public const string Count = "count";
    public const string Interval = "interval";
    public const string Size = "size";
    public const string Timeout = "timeout";
    public const string Ttl = "ttl";
    public const string DontFragment = "df";
}
