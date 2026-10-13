using StormMachine.App.ViewModels;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Capture;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Measurements;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Экран эфира: прослушивание мышью.
/// </summary>
/// <remarks>
/// Проверяется без окна: модель представления собирается на подставных источнике
/// захвата и окружении. Живой сети для этого не нужно, а права администратора —
/// тем более: экран обязан вести себя правильно и без них, и это главная
/// из проверок здесь.
/// </remarks>
public sealed class CapturePageTests
{
    private static readonly CaptureAdapter Card = new()
    {
        Id = "345A60CE68F7",
        Description = "Realtek Gaming 2.5GbE",
        MacAddress = "34-5A-60-CE-68-F7",
        SystemName = "Ethernet",
    };

    [Fact(DisplayName = "Без прав экран объясняет и предлагает перезапуск")]
    public async Task WithoutRights_OffersTheRestart()
    {
        var page = Page(new StubCapture(CaptureRefusal.NeedsElevation));

        await page.ActivateAsync();

        Assert.False(page.IsAvailable);
        Assert.True(page.CanAskForRights);
        Assert.False(page.CanStart);
        Assert.Contains("администратор", page.Availability, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Без источника захвата кнопка прав не предлагается")]
    public async Task WithoutSource_DoesNotOfferRights()
    {
        // Перезапуск с правами не поможет там, где слушать нечем вовсе. Кнопка,
        // которая ничего не чинит, хуже её отсутствия: человек её нажмёт.
        var page = Page(new StubCapture(CaptureRefusal.NoDriver));

        await page.ActivateAsync();

        Assert.False(page.CanAskForRights);
    }

    [Fact(DisplayName = "Услышанное раскладывается по таблицам")]
    public async Task Heard_FillsTheTables()
    {
        var page = Page(new StubCapture(CaptureRefusal.None));

        await page.ActivateAsync();

        Assert.True(page.IsAvailable);
        Assert.True(page.CanStart);

        await page.ListenCommand.ExecuteAsync(null);

        var neighbor = Assert.Single(page.Neighbors);

        Assert.Equal("SW-2.5G-1", neighbor.Name);
        Assert.Equal("ether13", neighbor.Port);
        Assert.Equal("LLDP", neighbor.Protocol);

        var server = Assert.Single(page.Servers);

        Assert.Equal("192.168.200.1", server.Address);
        Assert.Equal("192.168.200.1", server.Gateway);
    }

    [Fact(DisplayName = "Чужой шлюз отмечается, свой — нет")]
    public async Task StrangeGateway_IsMarked()
    {
        // Единственное утверждение о постороннем DHCP, которое продукт делает сам:
        // объявлен шлюз, которого машина не знает. Всё прочее — факты без вердикта.
        var page = Page(new StubCapture(CaptureRefusal.None, gateway: "192.168.200.77"));

        await page.ActivateAsync();
        await page.ListenCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(page.Servers).IsStranger);
    }

    [Fact(DisplayName = "Оговорка о пустом улове печатается всегда")]
    public async Task Caveat_IsAlwaysPrinted()
    {
        // «Не услышали» и «нет» — разные утверждения, и второе продукт делать
        // не вправе: сосед объявляется раз в полминуты.
        var page = Page(new StubCapture(CaptureRefusal.None, silent: true));

        await page.ActivateAsync();
        await page.ListenCommand.ExecuteAsync(null);

        Assert.Empty(page.Neighbors);
        Assert.NotNull(page.Caveat);
    }

    private static CapturePageViewModel Page(StubCapture capture)
    {
        var section = NavigationMap.Sections.First(s => s.Route == NavigationMap.Capture);

        return new CapturePageViewModel(section, new CaptureService(capture, new StubEnvironment()));
    }

    /// <summary>Источник захвата, который отвечает заготовкой.</summary>
    private sealed class StubCapture(
        CaptureRefusal refusal,
        string? gateway = "192.168.200.1",
        bool silent = false) : ICaptureProvider
    {
        public CaptureEngine Engine => CaptureEngine.WindowsBuiltIn;

        public CaptureRefusal Availability => refusal;

        public string? DriverDescription => "встроенный в Windows pktmon 10.0";

        public IReadOnlyList<CaptureAdapter> Adapters() => refusal == CaptureRefusal.None ? [Card] : [];

        public Task<CaptureResult> ListenAsync(
            CaptureAdapter adapter,
            CaptureOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaptureResult
            {
                Adapter = adapter,
                StartedUtc = DateTimeOffset.UnixEpoch,
                Duration = TimeSpan.FromSeconds(45),
                FramesSeen = silent ? 0 : 7,
                Unparsed = 0,
                Neighbors = silent
                    ?
                    []
                    :
                    [
                        new LinkNeighbor
                        {
                            Protocol = NeighborProtocol.Lldp,
                            Source = NeighborSource.Capture,
                            LocalIfIndex = 0,
                            RemoteName = "SW-2.5G-1",
                            RemotePort = "ether13",
                            RemoteDescription = "MikroTik RouterOS",
                        },
                    ],
                Dhcp = new DhcpFinding
                {
                    Sightings = silent
                        ?
                        []
                        :
                        [
                            new DhcpSighting
                            {
                                ServerAddress = "192.168.200.1",
                                ServerMac = "00-00-5E-00-01-C8",
                                Message = DhcpMessage.Offer,
                                OfferedAddress = "192.168.200.98",
                                OfferedGateway = gateway,
                                ObservedUtc = DateTimeOffset.UnixEpoch,
                            },
                        ],
                },
            });
    }

    /// <summary>Машина с одной картой и известным шлюзом.</summary>
    private sealed class StubEnvironment : INetworkEnvironment
    {
        public IReadOnlyList<NetworkAdapter> GetAdapters() =>
        [
            new()
            {
                Id = "{card}",
                Name = "Ethernet",
                Description = "Realtek Gaming 2.5GbE",
                Kind = AdapterKind.Physical,
                MacAddress = "34-5A-60-CE-68-F7",
                IPv4Address = "192.168.200.104",
                Gateways = ["192.168.200.1"],
                IsUp = true,
            },
        ];

        public NetworkAdapter? GetPrimaryAdapter() => GetAdapters()[0];

        public bool IsElevated => false;
    }
}
