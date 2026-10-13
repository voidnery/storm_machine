using StormMachine.Application.Abstractions;
using StormMachine.Capture.PktMon;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Measurements;

namespace StormMachine.Capture.UnitTests;

/// <summary>
/// Прослушивание встроенным захватом.
/// </summary>
/// <remarks>
/// Живая сеть в модульном тесте недопустима, а всё общение с pktmon идёт через
/// запуск процесса — значит, подменяется ровно он. Подставной pktmon записывает,
/// о чём его просили, и кладёт на место журнала заранее собранный файл: так
/// проверяется и порядок команд, и то, что выходит на другом конце.
/// </remarks>
public sealed class PktMonCaptureProviderTests
{
    private static readonly byte[] Cdp = Convert.FromHexString(
        "01000CCCCCCCD401C3420FAC0078AAAA0300000C200001792E5F0001000D53572D322E35472D31");

    private static readonly CaptureAdapter Adapter = new()
    {
        Id = "345A60CE68F7",
        Description = "Realtek Gaming 2.5GbE",
        MacAddress = "34-5A-60-CE-68-F7",
    };

    [Fact(DisplayName = "Фильтры ставятся на драйвер до запуска захвата")]
    public async Task Filters_ComeBeforeCapture()
    {
        var tool = new FakeTool(PcapNgFile.With(PcapNgFile.Microseconds, (Cdp, 1_000_000)));

        await Listen(tool);

        // Порядок важен: фильтр, поставленный после старта, не действует
        // на уже идущий захват, и в журнал попал бы весь трафик сегмента.
        var filters = tool.Calls.FindIndex(c => c.StartsWith("filter add lldp", StringComparison.Ordinal));
        var start = tool.Calls.FindIndex(c => c.StartsWith("start", StringComparison.Ordinal));

        Assert.True(filters >= 0 && start > filters);
        Assert.Contains(tool.Calls, c => c.Contains("-d 0x88CC", StringComparison.Ordinal));
        Assert.Contains(tool.Calls, c => c.Contains("-m 01-00-0C-CC-CC-CC", StringComparison.Ordinal));
        Assert.Contains(tool.Calls, c => c.Contains("-t UDP -p 67", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Слушаются все компоненты своей карты")]
    public async Task Components_AreTakenByMac()
    {
        var tool = new FakeTool(PcapNgFile.With(PcapNgFile.Microseconds, (Cdp, 1_000_000)));

        await Listen(tool);

        var start = tool.Calls.Single(c => c.StartsWith("start", StringComparison.Ordinal));

        Assert.Contains("--comp 50 56", start, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Один кадр, пришедший трижды, считается одним")]
    public async Task Echoes_AreCountedOnce()
    {
        // pktmon регистрирует пакет в каждой точке стека, через которую тот прошёл.
        // На хосте с Hyper-V это три-четыре копии одного объявления; показать их
        // как три кадра значило бы соврать о том, сколько всего услышано.
        var tool = new FakeTool(PcapNgFile.With(
            PcapNgFile.Microseconds,
            (Cdp, 1_000_000),
            (Cdp, 1_000_050),
            (Cdp, 1_000_090)));

        var result = await Listen(tool);

        Assert.Equal(1, result.FramesSeen);
        Assert.Single(result.Neighbors);
        Assert.Equal("SW-2.5G-1", result.Neighbors[0].RemoteName);
    }

    [Fact(DisplayName = "То же объявление через полминуты — не эхо")]
    public async Task LaterRepeat_IsNotAnEcho()
    {
        // Коммутатор объявляется каждые полминуты, и это настоящие кадры.
        // Окно снятия дублей не должно склеивать их в один.
        var tool = new FakeTool(PcapNgFile.With(
            PcapNgFile.Microseconds,
            (Cdp, 1_000_000),
            (Cdp, 31_000_000)));

        var result = await Listen(tool);

        Assert.Equal(2, result.FramesSeen);

        // Сосед при этом остаётся один: два объявления одного коммутатора —
        // это один коммутатор.
        Assert.Single(result.Neighbors);
    }

    [Fact(DisplayName = "Без прав говорится, что делать, а не что сломалось")]
    public async Task Forbidden_SaysWhatToDo()
    {
        var tool = new FakeTool([]) { Denied = true };
        var provider = new PktMonCaptureProvider(new FakeEnvironment(), new FakeSystem(elevated: false), tool);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ListenAsync(Adapter, new CaptureOptions { Duration = TimeSpan.Zero }));

        Assert.Contains("администратора", failure.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "После неудачного запуска машина остаётся как была")]
    public async Task FailedStart_CleansUp()
    {
        var tool = new FakeTool([]) { StartFails = true };
        var provider = new PktMonCaptureProvider(new FakeEnvironment(), new FakeSystem(elevated: true), tool);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ListenAsync(Adapter, new CaptureOptions { Duration = TimeSpan.Zero }));

        // Оставить фильтры и загруженный драйвер значило бы менять состояние чужой
        // машины после неудачи — продукт убирает за собой и тогда, когда у него
        // не вышло.
        Assert.Equal(["filter remove", "unload"], tool.Calls[^2..]);
    }

    [Fact(DisplayName = "Драйвер выгружается после прослушивания")]
    public async Task AfterListening_DriverIsUnloaded()
    {
        // Пока драйвер загружен, Windows показывает служебные фильтры NDIS как
        // сетевые интерфейсы — с MAC-адресами настоящих карт. Оператор увидел бы
        // в выборе адаптера «...WFP Native MAC Layer LightWeight Filter-0000»
        // вместо своей карты. Поймано боевым прогоном.
        var tool = new FakeTool(PcapNgFile.With(PcapNgFile.Microseconds, (Cdp, 1_000_000)));

        await Listen(tool);

        Assert.Equal("unload", tool.Calls[^1]);
    }

    [Fact(DisplayName = "Когда слушать не просили ничего, драйвер не поднимается")]
    public async Task NothingAsked_TouchesNothing()
    {
        var tool = new FakeTool([]);
        var provider = new PktMonCaptureProvider(new FakeEnvironment(), new FakeSystem(elevated: true), tool);

        var result = await provider.ListenAsync(
            Adapter,
            new CaptureOptions { Duration = TimeSpan.Zero, Neighbors = false, Dhcp = false });

        Assert.Empty(tool.Calls);
        Assert.Equal(0, result.FramesSeen);
    }

    [Fact(DisplayName = "Без прав возможность названа как «нужны права», а не «нет драйвера»")]
    public void Unelevated_AsksForRights()
    {
        var provider = new PktMonCaptureProvider(
            new FakeEnvironment(),
            new FakeSystem(elevated: false),
            new FakeTool([]));

        Assert.Equal(CaptureRefusal.NeedsElevation, provider.Availability);
        Assert.Equal(CaptureEngine.WindowsBuiltIn, provider.Engine);
    }

    [Fact(DisplayName = "Адаптеры видны и без прав администратора")]
    public void Adapters_ListWithoutRights()
    {
        var provider = new PktMonCaptureProvider(
            new FakeEnvironment(),
            new FakeSystem(elevated: false),
            new FakeTool([]));

        // Выбирать карту оператор должен до перезапуска с правами, а не после:
        // пустой список на этом экране выглядел бы как «слушать нечего».
        var adapters = provider.Adapters();

        Assert.Single(adapters);
        Assert.Equal("34-5A-60-CE-68-F7", adapters[0].MacAddress);
    }

    private static Task<CaptureResult> Listen(FakeTool tool)
    {
        var provider = new PktMonCaptureProvider(new FakeEnvironment(), new FakeSystem(elevated: true), tool);

        return provider.ListenAsync(Adapter, new CaptureOptions { Duration = TimeSpan.Zero });
    }

    /// <summary>pktmon, которого нет: отвечает заготовками и запоминает просьбы.</summary>
    private sealed class FakeTool(byte[] journal) : PktMonTool
    {
        private const string Listing = """
            Сетевые адаптеры:   ИД MAC-адрес         Имя
               50 34-5A-60-CE-68-F7 vEthernet (DefaultVirtLan)
               56 34-5A-60-CE-68-F7 Realtek Gaming 2.5GbE Family Controller #3
               53 00-15-5D-00-02-C8 vEthernet (Default Switch)
            """;

        internal List<string> Calls { get; } = [];

        internal bool Denied { get; init; }

        internal bool StartFails { get; init; }

        public override bool Exists => true;

        public override string? Version => "10.0.26100.3624";

        public override Task<PktMonAnswer> RunAsync(string arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments);

            if (Denied)
            {
                return Task.FromResult(new PktMonAnswer(5, "Отказано в доступе."));
            }

            if (arguments.StartsWith("list", StringComparison.Ordinal))
            {
                return Task.FromResult(new PktMonAnswer(0, Listing));
            }

            if (arguments.StartsWith("start", StringComparison.Ordinal) && StartFails)
            {
                return Task.FromResult(new PktMonAnswer(1, "Захват уже идёт."));
            }

            if (arguments.StartsWith("etl2pcap", StringComparison.Ordinal))
            {
                File.WriteAllBytes(Quoted(arguments, last: true), journal);
            }

            return Task.FromResult(new PktMonAnswer(0, string.Empty));
        }

        /// <summary>Путь из строки аргументов — он в кавычках.</summary>
        private static string Quoted(string arguments, bool last)
        {
            var parts = arguments.Split('"');

            return last ? parts[^2] : parts[1];
        }
    }

    private sealed class FakeEnvironment : INetworkEnvironment
    {
        public IReadOnlyList<NetworkAdapter> GetAdapters() =>
        [
            new()
            {
                Id = "{GUID}",
                Name = "Ethernet",
                Description = "Realtek Gaming 2.5GbE Family Controller #3",
                Kind = AdapterKind.Physical,
                MacAddress = "34-5A-60-CE-68-F7",
                IsUp = true,
            },
            new()
            {
                Id = "{GUID-2}",
                Name = "Отключённая",
                Description = "Выключенная карта",
                Kind = AdapterKind.Physical,
                MacAddress = "00-11-22-33-44-55",
                IsUp = false,
            },
        ];

        public NetworkAdapter? GetPrimaryAdapter() => GetAdapters()[0];

        public bool IsElevated => false;
    }

    private sealed class FakeSystem(bool elevated) : ISystemCapabilities
    {
        public bool IsElevated { get; } = elevated;

        public bool CanOpenRawSockets => false;
    }
}
