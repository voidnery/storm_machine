using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Topology;

namespace StormMachine.Application.Topology;

/// <summary>Что учитывать при построении карты.</summary>
public sealed record TopologyOptions
{
    /// <summary>Сколько последних трассировок брать в расчёт.</summary>
    /// <remarks>
    /// Не все подряд: маршрут меняется, и старые трассировки нарисовали бы на карте
    /// путь, которого больше нет. Свежие — то, что описывает сеть сейчас.
    /// </remarks>
    public int PathHistory { get; init; } = 5;

    /// <summary>Включать ли внешние узлы из трассировок.</summary>
    public bool IncludeExternalPaths { get; init; } = true;

    /// <summary>Виртуальные коммутаторы и VPN как отдельные сети.</summary>
    /// <remarks>
    /// По умолчанию включены: на машине разработчика их бывает больше, чем настоящих
    /// сетей, и прятать их значило бы показать карту, не совпадающую с тем,
    /// что видит операционная система.
    /// </remarks>
    public bool IncludeVirtualAdapters { get; init; } = true;

    public int CollapseThreshold { get; init; } = 12;

    public IReadOnlyList<string> ExpandedSubnets { get; init; } = [];
}

/// <summary>
/// Сборка карты сети из того, что уже собрано другими итерациями.
/// </summary>
/// <remarks>
/// Своих измерений не делает — и это намеренно. Инвентарь дал устройства, трассировки
/// дали внешние пути, сетевое окружение дало подсети; карта их <b>складывает</b>.
/// Отсюда следует, что она пересчитывается мгновенно и не требует новых действий
/// по чужой сети.
/// </remarks>
public sealed class TopologyService(
    IDeviceStore devices,
    IRunStore runs,
    INetworkEnvironment environment)
{
    private readonly IDeviceStore _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    private readonly IRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly INetworkEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public async Task<TopologyGraph> BuildAsync(
        TopologyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TopologyOptions();

        await _devices.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var inventory = await _devices.ListDevicesAsync(cancellationToken).ConfigureAwait(false);

        return TopologyGraph.Build(new TopologyInput
        {
            Devices = inventory,
            Subnets = ReadSubnets(options),
            Paths = options.IncludeExternalPaths
                ? await ReadPathsAsync(options.PathHistory, cancellationToken).ConfigureAwait(false)
                : [],
            CollapseThreshold = options.CollapseThreshold,
            ExpandedSubnets = options.ExpandedSubnets,
        });
    }

    private List<LocalSubnet> ReadSubnets(TopologyOptions options)
    {
        var subnets = new List<LocalSubnet>();

        foreach (var adapter in _environment.GetAdapters())
        {
            var virtualAdapter = adapter.Kind is AdapterKind.Virtual or AdapterKind.Vpn or AdapterKind.Tunnel;

            if (!adapter.IsUp
                || adapter.SubnetCidr is not { } cidr
                || adapter.Kind == AdapterKind.Loopback
                || (virtualAdapter && !options.IncludeVirtualAdapters))
            {
                continue;
            }

            subnets.Add(new LocalSubnet
            {
                Cidr = cidr,
                InterfaceName = adapter.Name,
                InterfaceAddress = adapter.IPv4Address,
                Gateways = adapter.Gateways,
                IsVirtual = virtualAdapter,
            });
        }

        return subnets;
    }

    /// <summary>
    /// Достаёт пути из сохранённых трассировок.
    /// </summary>
    /// <remarks>
    /// Берутся агрегаты по рядам, а не сырые сэмплы: ряды переживают политику хранения,
    /// и карта продолжает строиться по прогонам любой давности.
    /// </remarks>
    private async Task<List<PathObservation>> ReadPathsAsync(int limit, CancellationToken cancellationToken)
    {
        var paths = new List<PathObservation>();

        await _runs.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var summaries = await _runs
            .ListAsync(new RunQuery { Limit = Math.Max(1, limit), ProbeName = "trace" }, cancellationToken)
            .ConfigureAwait(false);

        foreach (var summary in summaries)
        {
            var run = await _runs.GetAsync(summary.Id, cancellationToken).ConfigureAwait(false);

            if (run is null)
            {
                continue;
            }

            var analysis = run.Samples.Count > 0
                ? PathAnalysis.Compute(run.Samples, run.Summary.ResolvedAddress)
                : PathAnalysis.FromSeries(run.Series, run.Summary.ResolvedAddress);

            var hops = analysis.Hops
                .Where(h => !h.IsSilent && h.Address is not null)
                .Select(h => h.Address!)
                .ToList();

            if (hops.Count == 0)
            {
                continue;
            }

            paths.Add(new PathObservation
            {
                Destination = run.Summary.ResolvedAddress ?? run.Summary.TargetDisplay,
                Hops = hops,
                ObservedUtc = run.Summary.StartedUtc,
                HasGaps = analysis.SilentHops > 0,
            });
        }

        return paths;
    }
}
