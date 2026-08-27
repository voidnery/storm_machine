using StormMachine.Domain.Discovery;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Проверки хранилища инвентаря.
/// </summary>
/// <remarks>
/// Здесь закрепляются два свойства, ради которых инвентарь устроен именно так:
/// снимок сканирования неизменяем, а сводный инвентарь пересчитывается и хранит
/// правку оператора. Расхождение этих двух представлений сделало бы историю
/// недостоверной, а правку — недолговечной.
/// </remarks>
public sealed class SqliteDeviceStoreTests : IDisposable
{
    private static readonly DateTimeOffset Morning = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Evening = new(2026, 8, 26, 21, 0, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteDeviceStoreTests()
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

    private SqliteDeviceStore CreateStore() => new(new SqliteRunStore(new StorageOptions
    {
        DatabasePath = _databasePath,
        ApplyRetentionOnStartup = false,
    }));

    private static Device Device(
        string address,
        string? mac = null,
        string? name = null,
        string? vendor = null,
        bool online = true,
        DateTimeOffset? at = null)
    {
        var observed = at ?? Morning;
        var evidence = new List<Evidence>();

        if (online)
        {
            evidence.Add(Evidence.Of(EvidenceSource.IcmpEcho, EvidenceKind.Alive, "да", observed));
        }

        if (mac is not null)
        {
            evidence.Add(Evidence.Of(EvidenceSource.ArpTable, EvidenceKind.MacAddress, mac, observed));
        }

        if (name is not null)
        {
            evidence.Add(Evidence.Of(EvidenceSource.Netbios, EvidenceKind.HostName, name, observed));
        }

        if (vendor is not null)
        {
            evidence.Add(Evidence.Of(EvidenceSource.Oui, EvidenceKind.Vendor, vendor, observed));
        }

        return Domain.Discovery.Device.FromEvidence(address, evidence, observed, observed, online);
    }

    private static DiscoveryScan Scan(
        string range,
        IReadOnlyList<Device> devices,
        DateTimeOffset? at = null) => new()
        {
            Id = Guid.NewGuid(),
            Range = range,
            InterfaceName = "тестовый",
            StartedUtc = at ?? Morning,
            CompletedUtc = (at ?? Morning).AddSeconds(4),
            Probed = 254,
            WasCancelled = false,
            Devices = devices,
        };

    [Fact]
    public async Task Scan_RoundTripsWithItsDevices()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var scan = Scan("192.168.1.0/24", [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF", "СЕРВЕР", "Synology")]);
        await store.SaveScanAsync(scan);

        var restored = await store.GetScanAsync(scan.Id);

        Assert.NotNull(restored);
        Assert.Equal("192.168.1.0/24", restored.Range);
        Assert.Equal(254, restored.Probed);

        var device = Assert.Single(restored.Devices);
        Assert.Equal("192.168.1.10", device.Address);
        Assert.Equal("AA-BB-CC-DD-EE-FF", device.MacAddress);
        Assert.Equal("СЕРВЕР", device.HostName);
        Assert.Equal("Synology", device.Vendor);
        Assert.True(device.IsOnline);
    }

    [Fact]
    public async Task Inventory_AccumulatesAcrossScans()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveScanAsync(Scan("192.168.1.0/24", [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF")]));
        await store.SaveScanAsync(Scan(
            "192.168.1.0/24",
            [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF", at: Evening), Device("192.168.1.20", "11-22-33-44-55-66", at: Evening)],
            Evening));

        var devices = await store.ListDevicesAsync();

        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.True(d.IsOnline));
    }

    [Fact]
    public async Task DeviceMissingFromLastScan_GoesOffline()
    {
        // Устройство, которого в последнем сканировании не нашлось, не исчезает
        // из инвентаря — оно перестаёт числиться доступным.
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveScanAsync(Scan(
            "192.168.1.0/24",
            [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF"), Device("192.168.1.20", "11-22-33-44-55-66")]));

        await store.SaveScanAsync(Scan(
            "192.168.1.0/24",
            [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF", at: Evening)],
            Evening));

        var devices = await store.ListDevicesAsync();

        Assert.Equal(2, devices.Count);
        Assert.True(devices.Single(d => d.Address == "192.168.1.10").IsOnline);
        Assert.False(devices.Single(d => d.Address == "192.168.1.20").IsOnline);
    }

    [Fact]
    public async Task DeviceOutsideScannedRange_IsLeftAlone()
    {
        // За пределами просканированного диапазона мы не смотрели. Объявлять тамошние
        // устройства недоступными значило бы утверждать то, чего не проверяли.
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveScanAsync(Scan("10.0.0.0/24", [Device("10.0.0.5", "AA-BB-CC-DD-EE-FF")]));
        await store.SaveScanAsync(Scan("192.168.1.0/24", [Device("192.168.1.10", "11-22-33-44-55-66", at: Evening)], Evening));

        var devices = await store.ListDevicesAsync();

        Assert.True(devices.Single(d => d.Address == "10.0.0.5").IsOnline);
    }

    [Fact]
    public async Task ManualName_SurvivesRescan()
    {
        // Главное свойство инвентаря. Правка оператора — свидетельство с наивысшим
        // весом, а не запись в поле, поэтому пересканирование её не затирает.
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveScanAsync(Scan("192.168.1.0/24", [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF", "NAS2")]));

        await store.PinAsync(
            "AA-BB-CC-DD-EE-FF",
            Evidence.Of(EvidenceSource.Manual, EvidenceKind.HostName, "Хранилище", Morning));

        await store.SaveScanAsync(Scan(
            "192.168.1.0/24",
            [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF", "NAS2", at: Evening)],
            Evening));

        var device = Assert.Single(await store.ListDevicesAsync());
        Assert.Equal("Хранилище", device.HostName);

        // Снимок при этом остаётся честным: в нём то, что наблюдали, а не то,
        // что назначил человек.
        var scans = await store.ListScansAsync();
        var latest = await store.GetScanAsync(scans[0].Id);

        Assert.NotNull(latest);
        Assert.Equal("NAS2", Assert.Single(latest.Devices).HostName);
    }

    [Fact]
    public async Task RepeatedPin_ReplacesThePreviousOne()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveScanAsync(Scan("192.168.1.0/24", [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF")]));

        await store.PinAsync(
            "AA-BB-CC-DD-EE-FF",
            Evidence.Of(EvidenceSource.Manual, EvidenceKind.HostName, "Первое", Morning));

        await store.PinAsync(
            "AA-BB-CC-DD-EE-FF",
            Evidence.Of(EvidenceSource.Manual, EvidenceKind.HostName, "Второе", Evening));

        // У одного поля один хозяин: иначе две правки подряд оставили бы в базе
        // противоречие, которое разрешалось бы сравнением строк.
        Assert.Equal("Второе", Assert.Single(await store.ListDevicesAsync()).HostName);
    }

    [Fact]
    public async Task DeviceWithSeveralAddresses_KeepsThemAll()
    {
        // Узел с двумя адресами — одно устройство по MAC, но оба адреса обязаны
        // сохраниться: иначе инвентарь показывает 74 устройства там, где сканирование
        // нашло 75 адресов, и разница ничем не объяснена.
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveScanAsync(Scan(
            "192.168.1.0/24",
            [Device("192.168.1.4", "AA-BB-CC-DD-EE-FF"), Device("192.168.1.5", "AA-BB-CC-DD-EE-FF")]));

        var device = Assert.Single(await store.ListDevicesAsync());

        Assert.Equal(2, device.Addresses.Count);
        Assert.Contains("192.168.1.4", device.Addresses);
        Assert.Contains("192.168.1.5", device.Addresses);

        // Основной адрес — наименьший, а не последний записанный: выбор должен быть
        // одинаковым при каждом пересчёте.
        Assert.Equal("192.168.1.4", device.Address);
    }

    [Fact]
    public async Task AddressesAccumulateAcrossScans()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveScanAsync(Scan("192.168.1.0/24", [Device("192.168.1.10", "AA-BB-CC-DD-EE-FF")]));
        await store.SaveScanAsync(Scan(
            "192.168.1.0/24",
            [Device("192.168.1.55", "AA-BB-CC-DD-EE-FF", at: Evening)],
            Evening));

        var device = Assert.Single(await store.ListDevicesAsync());

        // Устройство переехало по DHCP: прежний адрес остаётся в истории,
        // но устройство по-прежнему одно.
        Assert.Equal(2, device.Addresses.Count);
    }

    [Fact]
    public async Task ScansList_IsNewestFirst()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await store.SaveScanAsync(Scan("192.168.1.0/24", [Device("192.168.1.10")]));
        await store.SaveScanAsync(Scan("10.0.0.0/24", [Device("10.0.0.5")], Evening));

        var scans = await store.ListScansAsync();

        Assert.Equal(2, scans.Count);
        Assert.Equal("10.0.0.0/24", scans[0].Range);
    }

    [Fact]
    public async Task Audit_RecordsActiveActions()
    {
        // Сканирование чужой сети обязано оставлять след: требование раздела «Этика».
        var store = CreateStore();
        await store.InitializeAsync();

        await store.RecordAsync(new AuditEntry
        {
            Id = Guid.NewGuid(),
            AtUtc = Morning,
            Action = "discovery",
            Target = "192.168.1.0/24",
            Operator = "оператор",
            Details = "254 адреса",
        });

        var entry = Assert.Single(await store.ListAuditAsync());

        Assert.Equal("discovery", entry.Action);
        Assert.Equal("192.168.1.0/24", entry.Target);
        Assert.Equal("оператор", entry.Operator);
        Assert.Equal("254 адреса", entry.Details);
    }

    [Fact]
    public async Task EmptyInventory_IsNotAnError()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Assert.Empty(await store.ListDevicesAsync());
        Assert.Empty(await store.ListScansAsync());
        Assert.Empty(await store.ListAuditAsync());
        Assert.Null(await store.GetScanAsync(Guid.NewGuid()));
    }
}
