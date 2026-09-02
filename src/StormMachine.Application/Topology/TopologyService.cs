using StormMachine.Application.Abstractions;
using StormMachine.Application.Capture;
using StormMachine.Application.Snmp;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;
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

    /// <summary>
    /// Учитывать правки оператора.
    /// </summary>
    /// <remarks>
    /// Выключается только для проверки: полезно увидеть, что показывает сам инструмент,
    /// прежде чем спорить с ним. В обычной работе правки применяются всегда.
    /// </remarks>
    public bool ApplyOperatorEdits { get; init; } = true;

    /// <summary>
    /// Опрашивать ли оборудование по SNMP.
    /// </summary>
    /// <remarks>
    /// По умолчанию нет, и это не осторожность ради осторожности. Опрос идёт по чужой
    /// сети и занимает секунды на устройство; делать его молча при каждом взгляде
    /// на карту значило бы посылать трафик к оборудованию заказчика тогда, когда
    /// человек об этом не просил.
    /// </remarks>
    public bool UseSnmp { get; init; }

    /// <summary>
    /// Кого опрашивать. Пусто — шлюзы.
    /// </summary>
    /// <remarks>
    /// Шлюзы, потому что они наперечёт и почти всегда управляемы. Опрашивать всю
    /// подсеть подряд продукт не станет: перебор адресов с учётными данными — это
    /// уже не диагностика.
    /// </remarks>
    public IReadOnlyList<string> SnmpTargets { get; init; } = [];

    /// <summary>
    /// Слушать ли эфир, чтобы узнать, в чей порт воткнуты мы сами.
    /// </summary>
    /// <remarks>
    /// Выключено по умолчанию по той же причине, что и опрос: прослушивание занимает
    /// десятки секунд, и делать его при каждом взгляде на карту незачем. Но ответ
    /// оно даёт такой, какого не даёт больше никто: свой порт на коммутаторе видно
    /// без всяких учётных данных.
    /// </remarks>
    public bool UseCapture { get; init; }

    /// <summary>Сколько слушать. Меньше минуты — сосед может не успеть объявиться.</summary>
    public TimeSpan CaptureDuration { get; init; } = TimeSpan.FromSeconds(60);
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
    INetworkEnvironment environment,
    SnmpService? snmp = null,
    CaptureService? capture = null,
    ISettingsStore? settings = null)
{
    /// <summary>Ключ настройки со списком устройств, названных оператором.</summary>
    public const string RememberedDevicesKey = "topology.snmp.devices";

    private readonly IDeviceStore _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    private readonly IRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly INetworkEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly SnmpService? _snmp = snmp;
    private readonly CaptureService? _capture = capture;

    /// <summary>
    /// Настройки. Нужны ради одного списка — устройств, названных однажды.
    /// </summary>
    /// <remarks>
    /// Долг И-17: топология опрашивает шлюзы из маршрута, а коммутатор без адреса
    /// управления в этом маршруте приходилось называть ключом при <b>каждом</b> вызове.
    /// Обходить подсеть с учётными данными продукт не станет — это уже не диагностика, —
    /// но помнить названное однажды обязан.
    /// </remarks>
    private readonly ISettingsStore? _settings = settings;

    /// <summary>Устройства, которые оператор назвал и просил помнить.</summary>
    public async Task<IReadOnlyList<string>> RememberedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_settings is null)
        {
            return [];
        }

        try
        {
            var stored = await _settings.GetAsync(RememberedDevicesKey, cancellationToken).ConfigureAwait(false);

            return stored is null
                ? []
                : [.. stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Список — удобство, а не условие работы: карта строится и без него.
            return [];
        }
    }

    /// <summary>
    /// Запоминает названное устройство.
    /// </summary>
    /// <remarks>
    /// Порядок сохраняется: оператор перечисляет устройства в том порядке, в каком
    /// думает о своей сети, и пересортировывать их по алфавиту значило бы менять
    /// его картину на свою.
    /// </remarks>
    public async Task RememberDeviceAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (_settings is null)
        {
            return;
        }

        var known = (await RememberedDevicesAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var trimmed = address.Trim();

        if (known.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        known.Add(trimmed);

        await _settings
            .SetAsync(RememberedDevicesKey, string.Join(',', known), secret: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Забывает устройство. <c>false</c> — его и не помнили.</summary>
    public async Task<bool> ForgetDeviceAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (_settings is null)
        {
            return false;
        }

        var known = (await RememberedDevicesAsync(cancellationToken).ConfigureAwait(false)).ToList();

        if (known.RemoveAll(d => string.Equals(d, address.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        await _settings
            .SetAsync(RememberedDevicesKey, string.Join(',', known), secret: false, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <param name="note">
    /// Куда сообщать о ходе опроса. Опрос идёт секундами на устройство, и молчащий
    /// в это время инструмент выглядит зависшим.
    /// </param>
    public async Task<TopologyGraph> BuildAsync(
        TopologyOptions? options = null,
        Action<string>? note = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TopologyOptions();

        await _devices.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var inventory = await _devices.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        var subnets = ReadSubnets(options);

        return TopologyGraph.Build(new TopologyInput
        {
            Devices = inventory,
            Subnets = subnets,
            Switches = options.UseSnmp
                ? await PollAsync(options, subnets, note, cancellationToken).ConfigureAwait(false)
                : [],
            Neighbors = options.UseCapture
                ? await ListenAsync(options, note, cancellationToken).ConfigureAwait(false)
                : [],
            Paths = options.IncludeExternalPaths
                ? await ReadPathsAsync(options.PathHistory, cancellationToken).ConfigureAwait(false)
                : [],
            CollapseThreshold = options.CollapseThreshold,
            ExpandedSubnets = options.ExpandedSubnets,
            Edits = options.ApplyOperatorEdits
                ? await _devices.ListTopologyEditsAsync(cancellationToken).ConfigureAwait(false)
                : [],
        });
    }

    /// <summary>Доступен ли захват — чтобы предложить его, а не молчать.</summary>
    public bool CanUseCapture => _capture?.IsAvailable == true;

    /// <summary>
    /// Слушает эфир и отдаёт услышанных соседей.
    /// </summary>
    /// <remarks>
    /// Отсутствие драйвера здесь не отказ, а обычное дело: уровень 2 необязателен,
    /// и карта без него строится ровно как строилась.
    /// </remarks>
    private async Task<IReadOnlyList<LinkNeighbor>> ListenAsync(
        TopologyOptions options,
        Action<string>? note,
        CancellationToken cancellationToken)
    {
        if (_capture is null || !_capture.IsAvailable)
        {
            note?.Invoke(_capture?.Explain() ?? "Плагин захвата недоступен.");

            return [];
        }

        if (_capture.Primary() is not { } adapter)
        {
            note?.Invoke("Драйвер захвата не показывает подходящего адаптера.");

            return [];
        }

        note?.Invoke($"Слушаю {adapter.DisplayName} — "
                     + $"{options.CaptureDuration.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} с. "
                     + "Ничего в сеть не отправляется.");

        var result = await _capture
            .ListenAsync(adapter, new CaptureOptions { Duration = options.CaptureDuration }, cancellationToken)
            .ConfigureAwait(false);

        note?.Invoke(result.Neighbors.Count == 0
            ? "  соседей не услышано" + (result.Caveat is null ? "." : $": {result.Caveat}")
            : $"  услышано соседей: {result.Neighbors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

        return result.Neighbors;
    }

    /// <summary>Есть ли чем опрашивать — чтобы предложить это оператору, а не молчать.</summary>
    public Task<bool> CanUseSnmpAsync(CancellationToken cancellationToken = default) =>
        _snmp is null ? Task.FromResult(false) : _snmp.HasCredentialsAsync(cancellationToken);

    /// <summary>
    /// Опрашивает оборудование и складывает согласованные снимки.
    /// </summary>
    /// <remarks>
    /// Устройство, которое не ответило, пропускается с пометкой, а не роняет
    /// построение карты: SNMP выключен на половине оборудования, и карта без него
    /// всё равно строится — просто с догадками вместо фактов.
    /// </remarks>
    private async Task<IReadOnlyList<SnmpDevice>> PollAsync(
        TopologyOptions options,
        IReadOnlyList<LocalSubnet> subnets,
        Action<string>? note,
        CancellationToken cancellationToken)
    {
        if (_snmp is null)
        {
            return [];
        }

        // Названное оператором добавляется к шлюзам, а не заменяет их: коммутатор
        // без адреса управления в маршруте по умолчанию не отменяет сам маршрут.
        // Явно переданное в этом вызове идёт первым — оно и есть то, о чём спросили
        // прямо сейчас.
        var remembered = await RememberedDevicesAsync(cancellationToken).ConfigureAwait(false);

        var targets = options.SnmpTargets.Count > 0
            ? [.. options.SnmpTargets]
            : new List<string>();

        foreach (var address in subnets.SelectMany(s => s.Gateways).Concat(remembered))
        {
            if (!targets.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(address);
            }
        }

        if (targets.Count == 0)
        {
            note?.Invoke("Опрашивать некого: шлюзов не найдено, адреса не заданы.");

            return [];
        }

        if (remembered.Count > 0 && options.SnmpTargets.Count == 0)
        {
            note?.Invoke($"Помню устройства: {string.Join(", ", remembered)}. "
                         + "Забыть — storm topology forget <адрес>.");
        }

        var found = new List<SnmpDevice>();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            note?.Invoke($"Опрашиваю {target}…");

            try
            {
                var reach = await _snmp.ProbeAsync(target, cancellationToken).ConfigureAwait(false);

                if (reach is null)
                {
                    note?.Invoke($"  {target}: не ответил ни одним из заведённых наборов.");

                    continue;
                }

                var device = await _snmp
                    .InspectAsync(target, reach.Credential, cancellationToken)
                    .ConfigureAwait(false);

                found.Add(device);

                note?.Invoke(
                    $"  {target}: {device.DisplayName}, портов "
                    + $"{device.Interfaces.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}, "
                    + $"соседей {device.Neighbors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}, "
                    + $"адресов в таблице {device.Forwarding.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
            }
            catch (SnmpException ex)
            {
                note?.Invoke($"  {target}: {ex.Message}");
            }
        }

        return found;
    }

    private List<LocalSubnet> ReadSubnets(TopologyOptions options)
    {
        var subnets = new List<LocalSubnet>();

        foreach (var adapter in _environment.GetAdapters())
        {
            var virtualAdapter = AdapterWording.IsUntrustworthy(adapter.Kind);

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
