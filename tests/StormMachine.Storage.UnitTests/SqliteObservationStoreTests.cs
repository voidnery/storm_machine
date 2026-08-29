using StormMachine.Application.Abstractions;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Results;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// История наблюдений за оборудованием.
/// </summary>
/// <remarks>
/// Появилась в И-21 и закрыла два одинаковых долга — И-17 и И-18. Оба вида данных
/// продукт читать умел, а хранить не умел: загрузка порта мерилась на месте
/// и показывалась, услышанные соседи и серверы DHCP показывались и забывались.
/// <para>
/// Главное, что здесь проверяется, — <b>время первого наблюдения</b>. Именно оно
/// отвечает на вопрос, ради которого история и ведётся: посторонний сервер DHCP сам
/// по себе не доказательство, два сервера в одном домене бывают и законно, — а вот
/// сервер, появившийся вчера, это уже событие.
/// </para>
/// </remarks>
public sealed class SqliteObservationStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteObservationStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "storm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "storm.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Файл мог остаться заблокированным — временный каталог уберёт система.
        }
    }

    private SqliteObservationStore CreateStore() => new(new SqliteRunStore(new StorageOptions
    {
        DatabasePath = _databasePath,
        Retention = RetentionPolicy.Default,
        ApplyRetentionOnStartup = false,
    }));

    // ------------------------------------------------------------ загрузка портов

    private static PortLoadPoint Point(
        DateTimeOffset at,
        int ifIndex = 1,
        double inBps = 1_000_000,
        long errors = 0) => new()
    {
        Device = "10.0.0.1",
        IfIndex = ifIndex,
        IfName = $"GigabitEthernet0/{ifIndex}",
        AtUtc = at,
        Interval = TimeSpan.FromSeconds(10),
        InBitsPerSecond = inBps,
        OutBitsPerSecond = inBps / 2,
        SpeedBitsPerSecond = 1_000_000_000,
        InErrors = errors,
    };

    [Fact]
    public async Task PortLoad_IsStoredAndReadBackAsASeries()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var start = DateTimeOffset.UnixEpoch;

        await store.SavePortLoadAsync(
        [
            Point(start),
            Point(start.AddSeconds(10)),
            Point(start.AddSeconds(20)),
        ]);

        var series = await store.ListPortLoadAsync("10.0.0.1", ifIndex: 1, start);

        Assert.Equal(3, series.Count);

        // Ряд возвращается по возрастанию времени: это график, а не список.
        Assert.True(series[0].AtUtc < series[1].AtUtc);
        Assert.True(series[1].AtUtc < series[2].AtUtc);
    }

    /// <summary>Проценты считаются от скорости порта, а без неё не считаются вовсе.</summary>
    [Fact]
    public async Task PortLoad_KeepsWhatIsNeededToComputePercent()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SavePortLoadAsync([Point(DateTimeOffset.UnixEpoch, inBps: 100_000_000)]);

        var point = (await store.ListPortLoadAsync("10.0.0.1", null, DateTimeOffset.UnixEpoch)).Single();

        Assert.Equal(10.0, point.InPercent!.Value, 3);
        Assert.Equal(5.0, point.OutPercent!.Value, 3);
    }

    [Fact]
    public async Task PortLoad_WithoutSpeedDoesNotPretendToKnowPercent()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SavePortLoadAsync([Point(DateTimeOffset.UnixEpoch) with { SpeedBitsPerSecond = 0 }]);

        var point = (await store.ListPortLoadAsync(null, null, DateTimeOffset.UnixEpoch)).Single();

        Assert.Null(point.InPercent);
        Assert.Null(point.OutPercent);
    }

    /// <summary>Порты разделяются: ряд одного не смешивается с рядом другого.</summary>
    [Fact]
    public async Task PortLoad_SeparatesPorts()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var start = DateTimeOffset.UnixEpoch;

        await store.SavePortLoadAsync([Point(start, ifIndex: 1), Point(start, ifIndex: 2)]);

        Assert.Single(await store.ListPortLoadAsync("10.0.0.1", 1, start));
        Assert.Equal(2, (await store.ListPortLoadAsync("10.0.0.1", null, start)).Count);
    }

    /// <summary>
    /// Ошибки и отбросы складываются в одно число.
    /// </summary>
    /// <remarks>
    /// По нему и судят о состоянии кабеля: растущий счётчик ошибок — первый признак
    /// умирающего патч-корда, и различать входящие от исходящих на этом этапе незачем.
    /// </remarks>
    [Fact]
    public async Task PortLoad_SumsFaults()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SavePortLoadAsync(
        [
            Point(DateTimeOffset.UnixEpoch) with { InErrors = 3, OutErrors = 2, InDiscards = 1, OutDiscards = 4 },
        ]);

        var point = (await store.ListPortLoadAsync(null, null, DateTimeOffset.UnixEpoch)).Single();

        Assert.Equal(10, point.Faults);
    }

    // ------------------------------------------------------------ услышанное

    private static CaptureResult Capture(DateTimeOffset at, string server = "192.168.1.1", string gateway = "192.168.1.1") => new()
    {
        Adapter = new CaptureAdapter
        {
            Id = @"\Device\NPF_{TEST}",
            Description = "Тестовый адаптер",
            SystemName = "Ethernet",
        },
        Duration = TimeSpan.FromSeconds(30),
        StartedUtc = at,
        Neighbors =
        [
            new LinkNeighbor
            {
                Protocol = NeighborProtocol.Lldp,
                Source = NeighborSource.Capture,
                LocalIfIndex = 1,
                RemoteChassisId = "00:11:22:33:44:55",
                RemotePort = "Gi0/24",
                RemoteName = "switch-1",
                ObservedUtc = at,
            },
        ],
        Dhcp = new DhcpFinding
        {
            Sightings =
            [
                new DhcpSighting
                {
                    ServerAddress = server,
                    Message = DhcpMessage.Offer,
                    OfferedGateway = gateway,
                    OfferedDns = ["8.8.8.8", "1.1.1.1"],
                    ObservedUtc = at,
                },
            ],
        },
    };

    /// <summary>
    /// Повторное наблюдение обновляет последнее время и не трогает первое.
    /// </summary>
    /// <remarks>
    /// Это и есть главное утверждение файла. Сосед, услышанный сто раз, — это один
    /// сосед; а вот когда он появился впервые, продукт обязан помнить точно, иначе
    /// история не отвечает ни на один вопрос, ради которого её ведут.
    /// </remarks>
    [Fact]
    public async Task HeardTwice_KeepsTheFirstSightingIntact()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var first = DateTimeOffset.UnixEpoch;
        var later = first.AddDays(3);

        await store.SaveCaptureAsync(Capture(first));
        await store.SaveCaptureAsync(Capture(later));

        var neighbor = (await store.ListNeighborsAsync(first)).Single();

        Assert.Equal(first, neighbor.FirstSeenUtc);
        Assert.Equal(later, neighbor.LastSeenUtc);

        var dhcp = (await store.ListDhcpAsync(first)).Single();

        Assert.Equal(first, dhcp.FirstSeenUtc);
        Assert.Equal(later, dhcp.LastSeenUtc);
        Assert.Equal(2, dhcp.Sightings);
    }

    /// <summary>
    /// Сервер, сменивший объявляемый шлюз, попадает в историю отдельной записью.
    /// </summary>
    /// <remarks>
    /// Ровно то событие, ради которого захват и слушают. Обновить строку на месте
    /// значило бы его потерять: продукт показал бы текущий шлюз и умолчал о том,
    /// что вчера тот же сервер объявлял другой.
    /// </remarks>
    [Fact]
    public async Task ServerChangingItsGateway_IsRecordedSeparately()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var first = DateTimeOffset.UnixEpoch;

        await store.SaveCaptureAsync(Capture(first, gateway: "192.168.1.1"));
        await store.SaveCaptureAsync(Capture(first.AddDays(1), gateway: "192.168.1.254"));

        var servers = await store.ListDhcpAsync(first);

        Assert.Equal(2, servers.Count);
        Assert.Contains(servers, s => s.OfferedGateway == "192.168.1.1");
        Assert.Contains(servers, s => s.OfferedGateway == "192.168.1.254");
    }

    [Fact]
    public async Task Dhcp_KeepsTheOfferedResolvers()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveCaptureAsync(Capture(DateTimeOffset.UnixEpoch));

        var dhcp = (await store.ListDhcpAsync(DateTimeOffset.UnixEpoch)).Single();

        Assert.Equal(["8.8.8.8", "1.1.1.1"], dhcp.OfferedDns);
    }

    /// <summary>Соседи разных серверов не сливаются в одного.</summary>
    [Fact]
    public async Task TwoServers_StayTwo()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var at = DateTimeOffset.UnixEpoch;

        await store.SaveCaptureAsync(Capture(at, server: "192.168.1.1", gateway: "192.168.1.1"));
        await store.SaveCaptureAsync(Capture(at, server: "192.168.1.50", gateway: "192.168.1.1"));

        Assert.Equal(2, (await store.ListDhcpAsync(at)).Count);
    }

    // ------------------------------------------------------------------- уборка

    /// <summary>
    /// Уборка судит соседей по последнему наблюдению, а не по первому.
    /// </summary>
    /// <remarks>
    /// Различие существенное: сосед, впервые услышанный год назад и слышимый до сих
    /// пор, — это действующее соседство. Удалить его как старое значило бы стереть
    /// самое достоверное, что есть на карте.
    /// </remarks>
    [Fact]
    public async Task Retention_JudgesNeighborsByTheLastSighting()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var longAgo = DateTimeOffset.UtcNow.AddDays(-400);

        await store.SaveCaptureAsync(Capture(longAgo));
        await store.SaveCaptureAsync(Capture(DateTimeOffset.UtcNow));

        await store.ApplyRetentionAsync(TimeSpan.FromDays(365));

        var neighbors = await store.ListNeighborsAsync(longAgo);

        Assert.Single(neighbors);
        Assert.Equal(longAgo, neighbors[0].FirstSeenUtc);
    }

    [Fact]
    public async Task Retention_RemovesOldPortLoad()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var longAgo = DateTimeOffset.UtcNow.AddDays(-400);

        await store.SavePortLoadAsync([Point(longAgo), Point(DateTimeOffset.UtcNow)]);

        var removed = await store.ApplyRetentionAsync(TimeSpan.FromDays(365));

        Assert.Equal(1, removed);
        Assert.Single(await store.ListPortLoadAsync(null, null, longAgo));
    }

    /// <summary>Пустой список не открывает соединение и не падает.</summary>
    [Fact]
    public async Task SavingNothing_IsNotAnError()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SavePortLoadAsync([]);

        Assert.Empty(await store.ListPortLoadAsync(null, null, DateTimeOffset.UnixEpoch));
    }
}
