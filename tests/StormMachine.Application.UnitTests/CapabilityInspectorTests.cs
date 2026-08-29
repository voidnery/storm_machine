using StormMachine.Application.Abstractions;
using StormMachine.Application.Capabilities;
using StormMachine.Application.Probes;
using StormMachine.Domain.Agents;
using StormMachine.Domain.Capabilities;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Сводка возможностей.
/// </summary>
/// <remarks>
/// Проверяется главное обещание экрана: он говорит правду о <b>этой</b> машине.
/// Сводка, собранная из намерений вместо фактов, хуже отсутствующей — она обещает
/// за чужую систему, и оператор узнаёт цену обещания на объекте у заказчика.
/// </remarks>
public sealed class CapabilityInspectorTests
{
    private static ProbeDescriptor Descriptor(
        string name,
        bool elevation = false,
        bool agent = false) => new()
    {
        Kind = ProbeKind.Icmp,
        Name = name,
        Title = name,
        Description = "проба для теста",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.IcmpEcho,
        Parameters = [],
        RequiresElevation = elevation,
        RequiresAgent = agent,
    };

    private static CapabilityInspector Inspector(
        IReadOnlyList<ProbeDescriptor> probes,
        bool elevated = false,
        bool rawSockets = true,
        bool captureDriver = false,
        int agents = 0) =>
        new(new DescriptorRegistry(probes),
            new FakeSystemCapabilities
            {
                IsElevated = elevated,
                CanOpenRawSockets = rawSockets,
            },
            new FakeAgentStore(agents),
            new FakeEnvironment(),
            new FakeCapture(captureDriver));

    private static Capability Find(CapabilityReport report, string id) =>
        report.Capabilities.Single(c => c.Id == id);

    [Fact(DisplayName = "Проба, требующая прав, без них показана недоступной с причиной")]
    public async Task ElevationIsReported()
    {
        var report = await Inspector([Descriptor("traceroute", elevation: true)]).InspectAsync();

        var probe = Find(report, "probe.traceroute");

        Assert.Equal(CapabilityState.NeedsElevation, probe.State);
        Assert.False(probe.IsUsable);
        Assert.Contains("администратора", probe.HowToEnable, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Та же проба с правами доступна")]
    public async Task ElevationSatisfied()
    {
        var report = await Inspector([Descriptor("traceroute", elevation: true)], elevated: true).InspectAsync();

        Assert.Equal(CapabilityState.Available, Find(report, "probe.traceroute").State);
        Assert.True(report.IsElevated);
    }

    [Fact(DisplayName = "Проба между двумя точками без агента ждёт агента, а не прав")]
    public async Task AgentIsReported()
    {
        // Разница существенна: перезапуск от администратора здесь не поможет,
        // и предложить его значило бы отправить оператора не туда.
        var report = await Inspector([Descriptor("throughput", agent: true)], elevated: true).InspectAsync();

        var probe = Find(report, "probe.throughput");

        Assert.Equal(CapabilityState.NeedsAgent, probe.State);
        Assert.Contains("storm agents pair", probe.HowToEnable, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Сопряжённый агент открывает пробу между двумя точками")]
    public async Task AgentSatisfied()
    {
        var report = await Inspector([Descriptor("throughput", agent: true)], agents: 1).InspectAsync();

        Assert.Equal(CapabilityState.Available, Find(report, "probe.throughput").State);
    }

    [Fact(DisplayName = "Список проб берётся из реестра, а не переписан руками")]
    public async Task ProbesComeFromRegistry()
    {
        // Иначе экран возможностей отстал бы от продукта на первой же итерации —
        // и врал бы именно там, где обещает честность.
        var report = await Inspector([Descriptor("новая-проба")]).InspectAsync();

        Assert.Contains(report.Capabilities, c => c.Id == "probe.новая-проба");
    }

    [Fact(DisplayName = "Уровень с одной недоступной возможностью не объявляется доступным")]
    public async Task LevelTakesTheWorst()
    {
        // Оператор, прочитавший «доступно», упрётся в неработающую половину
        // в самый неподходящий момент.
        var report = await Inspector([Descriptor("ping"), Descriptor("traceroute", elevation: true)]).InspectAsync();

        Assert.Equal(CapabilityState.Limited, report.StateOf(CapabilityLevel.Core));
    }

    [Fact(DisplayName = "Без сырых сокетов измерения объявлены огрублёнными, а не сломанными")]
    public async Task RawSocketsAreLimitedNotBroken()
    {
        var report = await Inspector([Descriptor("ping")], rawSockets: false).InspectAsync();

        var raw = Find(report, "core.raw-sockets");

        Assert.Equal(CapabilityState.Limited, raw.State);
        Assert.True(raw.IsUsable);
        Assert.Contains("грубее", raw.Detail, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Npcap не обещан к поставке ни при каких условиях")]
    public async Task CaptureDriverIsNeverBundled()
    {
        // Лицензия NPSL распространение запрещает (решение R-02 исследования).
        // Строка на экране — единственное место, где это видит оператор.
        var report = await Inspector([Descriptor("ping")]).InspectAsync();

        var capture = Find(report, "capture.neighbors");

        Assert.Equal(CapabilityState.NeedsDriver, capture.State);
        Assert.Contains("не распространяет", capture.HowToEnable, StringComparison.Ordinal);
        Assert.Equal(CapabilityInspector.CaptureDriverSite, capture.Where);
    }

    [Fact(DisplayName = "С драйвером возможности уровня 2 появляются")]
    public async Task DriverEnablesCapture()
    {
        // Условие приёмки И-18: без Npcap продукт работает и честно говорит, чего
        // не хватает; с Npcap функции появляются. До И-18 здесь стояло «запланировано»,
        // потому что плагина не было, — теперь он есть, и проверка это закрепляет.
        var report = await Inspector([Descriptor("ping")], captureDriver: true).InspectAsync();

        Assert.Equal(CapabilityState.Available, Find(report, "capture.neighbors").State);
        Assert.Equal(CapabilityState.Available, Find(report, "capture.dhcp").State);
        Assert.Equal(CapabilityState.Available, report.StateOf(CapabilityLevel.Capture));

        var capture = Find(report, "capture.neighbors");

        Assert.Contains("Npcap 1.79", capture.Detail, StringComparison.Ordinal);
        Assert.Null(capture.Where);
    }

    [Fact(DisplayName = "Драйвер, не пускающий без прав, отличается от отсутствующего")]
    public async Task DeniedDriverIsDistinct()
    {
        // Два разных совета: в одном случае ставить драйвер, в другом — перезапуститься
        // администратором. Склеить их в «недоступно» значит послать человека вслепую.
        var inspector = new CapabilityInspector(
            new DescriptorRegistry([Descriptor("ping")]),
            new FakeSystemCapabilities(),
            new FakeAgentStore(0),
            new FakeEnvironment(),
            new FakeCapture(CaptureRefusal.NeedsElevation));

        var capture = Find(await inspector.InspectAsync(), "capture.neighbors");

        Assert.Equal(CapabilityState.NeedsElevation, capture.State);
        Assert.Contains("администратора", capture.HowToEnable, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "SNMP без учётных данных ждёт их, а не числится запланированным")]
    public async Task SnmpNeedsCredentials()
    {
        // До И-17 здесь стояло «запланировано». Оставить это после того, как опрос
        // сделан, — та же ложь, что спрятать недоступное.
        var capture = Find(await Inspector([Descriptor("ping")]).InspectAsync(), "snmp");

        Assert.Equal(CapabilityState.NeedsCredentials, capture.State);
        Assert.Contains("storm snmp creds add", capture.HowToEnable, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Недоступное хранилище агентов не роняет сводку")]
    public async Task BrokenAgentStoreIsSurvivable()
    {
        var inspector = new CapabilityInspector(
            new DescriptorRegistry([Descriptor("throughput", agent: true)]),
            new FakeSystemCapabilities(),
            new BrokenAgentStore(),
            new FakeEnvironment());

        var report = await inspector.InspectAsync();

        Assert.Equal(CapabilityState.NeedsAgent, Find(report, "probe.throughput").State);
    }

    // ------------------------------------------------------------------ дублёры

    private sealed class DescriptorRegistry(IReadOnlyList<ProbeDescriptor> descriptors) : IProbeRegistry
    {
        public IReadOnlyList<ProbeDescriptor> Descriptors { get; } = descriptors;

        public bool TryGet(string name, out IProbe found)
        {
            found = null!;

            return false;
        }
    }

    /// <summary>Плагин захвата, отвечающий заданным состоянием.</summary>
    private sealed class FakeCapture(CaptureRefusal refusal) : ICaptureProvider
    {
        public FakeCapture(bool driver)
            : this(driver ? CaptureRefusal.None : CaptureRefusal.NoDriver)
        {
        }

        public CaptureRefusal Availability { get; } = refusal;

        public string? DriverDescription => Availability == CaptureRefusal.NoDriver ? null : "Npcap 1.79";

        public IReadOnlyList<CaptureAdapter> Adapters() => [];

        public Task<CaptureResult> ListenAsync(
            CaptureAdapter adapter,
            CaptureOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSystemCapabilities : ISystemCapabilities
    {
        public bool IsElevated { get; init; }

        public bool IsCaptureDriverInstalled { get; init; }

        public string? CaptureDriverDescription { get; init; }

        public bool CanOpenRawSockets { get; init; } = true;
    }

    private sealed class FakeAgentStore(int count) : IAgentStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<byte[]?> LoadIdentityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task SaveIdentityAsync(byte[] container, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RemoteAgent>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteAgent>>(
            [
                .. Enumerable.Range(0, count).Select(i => new RemoteAgent
                {
                    Thumbprint = i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    MachineName = "агент",
                    Product = "storm-agent",
                    Direction = AgentDirection.ClientDials,
                    PairedUtc = DateTimeOffset.UtcNow,
                }),
            ]);

        public Task<RemoteAgent?> FindAsync(string thumbprintOrName, CancellationToken cancellationToken = default) =>
            Task.FromResult<RemoteAgent?>(null);

        public Task SaveAsync(RemoteAgent agent, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ForgetAsync(string thumbprint, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class BrokenAgentStore : IAgentStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<byte[]?> LoadIdentityAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("база недоступна");

        public Task SaveIdentityAsync(byte[] container, CancellationToken cancellationToken = default) =>
            throw new IOException("база недоступна");

        public Task<IReadOnlyList<RemoteAgent>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("база недоступна");

        public Task<RemoteAgent?> FindAsync(string thumbprintOrName, CancellationToken cancellationToken = default) =>
            throw new IOException("база недоступна");

        public Task SaveAsync(RemoteAgent agent, CancellationToken cancellationToken = default) =>
            throw new IOException("база недоступна");

        public Task<bool> ForgetAsync(string thumbprint, CancellationToken cancellationToken = default) =>
            throw new IOException("база недоступна");
    }

    private sealed class FakeEnvironment : INetworkEnvironment
    {
        public IReadOnlyList<NetworkAdapter> GetAdapters() => [Primary];

        public NetworkAdapter? GetPrimaryAdapter() => Primary;

        public bool IsElevated => false;

        private static NetworkAdapter Primary { get; } = new()
        {
            Id = "eth0",
            Name = "Ethernet",
            Description = "адаптер для теста",
            Kind = AdapterKind.Physical,
            IPv4Address = "192.168.1.10",
            PrefixLength = 24,
            Gateways = ["192.168.1.1"],
            IsUp = true,
        };
    }
}
