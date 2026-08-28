using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Application.Runs;

/// <summary>Как выполнять прогон.</summary>
public sealed record RunOptions
{
    /// <summary>Сохранять ли результат в журнал.</summary>
    public bool Save { get; init; }

    /// <summary>Вызывается на каждый сэмпл по мере поступления — для живого вывода.</summary>
    public Action<Sample>? OnSample { get; init; }

    /// <summary>Пресет, из которого идёт запуск. Попадает в журнал вместе с прогоном.</summary>
    public Guid? PresetId { get; init; }

    public int? PresetVersion { get; init; }
}

/// <summary>Чем закончился прогон.</summary>
public sealed record RunOutcome
{
    public required ProbeResult Result { get; init; }

    /// <summary>Идентификатор записи в журнале, если прогон сохранялся.</summary>
    public Guid? RunId { get; init; }
}

/// <summary>
/// Выполнение пробы: подготовка условий, живой поток, запись в журнал, итог.
/// </summary>
/// <remarks>
/// Живёт в слое приложения, а не в клиенте, потому что нужен всем троим одинаково:
/// консоли, графическому клиенту и — в будущем — планировщику. Клиент отвечает только
/// за показ.
/// </remarks>
public sealed class RunOrchestrator(
    IRunStore store,
    IHighResolutionClock clock,
    INetworkEnvironment environment,
    IProfileStore? profiles = null)
{
    private readonly IRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly INetworkEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>
    /// Профили. Необязательны: продукт полностью работоспособен и без них.
    /// </summary>
    /// <remarks>
    /// Нужны здесь ради одной строки в условиях измерения — имени активного профиля.
    /// Через полгода отличить замер у заказчика от замера в офисе иначе будет нечем,
    /// а сравнивать их между собой нельзя.
    /// </remarks>
    private readonly IProfileStore? _profiles = profiles;

    public async Task<RunOutcome> RunAsync(
        IProbe probe,
        ProbeRequest request,
        RunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var descriptor = probe.Descriptor;

        await _clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);

        var adapter = _environment.GetPrimaryAdapter();
        var context = BuildContext(adapter, descriptor.Methodology, await ProfileAsync(cancellationToken).ConfigureAwait(false));
        var collector = new ProbeCollector();
        var samples = new List<Sample>();

        IRunWriter? writer = null;

        if (options.Save)
        {
            writer = await _store.BeginRunAsync(
                new RunDescriptor
                {
                    Kind = descriptor.Kind,
                    ProbeName = descriptor.Name,
                    Shape = descriptor.Shape,
                    Target = request.Target,
                    Context = context,
                    Unit = descriptor.Unit,
                    Parameters = request.Parameters,
                    PresetId = options.PresetId,
                    PresetVersion = options.PresetVersion,
                },
                cancellationToken).ConfigureAwait(false);
        }

        var wasCancelled = false;

        try
        {
            try
            {
                await foreach (var sample in probe
                    .ExecuteAsync(request, collector, cancellationToken)
                    .ConfigureAwait(false))
                {
                    samples.Add(sample);
                    options.OnSample?.Invoke(sample);

                    if (writer is not null)
                    {
                        // Запись идёт по ходу, а не в конце: прогон, оборванный
                        // отменой или падением процесса, обязан сохранить измеренное.
                        await writer.AppendAsync(sample, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }

            var result = new ProbeResult
            {
                Id = writer?.RunId ?? Guid.NewGuid(),
                Kind = descriptor.Kind,
                Target = request.Target,
                ResolvedAddress = collector.ResolvedAddress,
                Context = context,
                Unit = descriptor.Unit,
                Samples = samples,
                Facts = collector.Facts,
                CompletedUtc = DateTimeOffset.UtcNow,
                WasCancelled = wasCancelled,
            };

            if (writer is not null)
            {
                // CancellationToken.None намеренно: подведение итога не должно
                // сорваться по той же отмене, которая прервала измерение.
                await writer
                    .CompleteAsync(collector.Facts, collector.ResolvedAddress, wasCancelled, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return new RunOutcome { Result = result, RunId = writer?.RunId };
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Имя активного профиля. Сбой чтения не должен срывать измерение.</summary>
    private async Task<string?> ProfileAsync(CancellationToken cancellationToken)
    {
        if (_profiles is null)
        {
            return null;
        }

        try
        {
            return (await _profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false))?.Name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private MeasurementContext BuildContext(
        NetworkAdapter? adapter,
        Methodology methodology,
        string? profile) => new()
    {
        InterfaceName = adapter?.Name ?? "неизвестен",
        AdapterKind = adapter?.Kind ?? AdapterKind.Unknown,
        InterfaceAddress = adapter?.IPv4Address,
        CalibrationBaselineMs = _clock.CalibrationBaselineMs,
        ProductVersion = ProductInfo.Version,
        Methodology = methodology,
        Profile = profile,
        StartedUtc = DateTimeOffset.UtcNow,
    };
}
