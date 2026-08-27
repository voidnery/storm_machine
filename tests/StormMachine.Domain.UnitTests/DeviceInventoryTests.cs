using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки инвентаря: слияние свидетельств и различия между сканированиями.
/// </summary>
/// <remarks>
/// Два свойства, ради которых инвентарь устроен именно так, и оба проверяются здесь:
/// правка оператора переживает пересканирование, а смена адреса по DHCP не выглядит
/// исчезновением одного устройства и появлением другого.
/// </remarks>
public sealed class DeviceInventoryTests
{
    private static readonly int[] WebAndSmb = [80, 445];

    private static readonly string[] TwoAddresses = ["192.168.1.4", "192.168.1.5"];

    private static readonly string[] SecondAddressOnly = ["192.168.1.5"];

    private static readonly string[] OneAddress = ["192.168.1.10"];

    private static readonly DateTimeOffset Morning = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Evening = new(2026, 8, 26, 21, 0, 0, TimeSpan.Zero);

    private static Device Make(
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

        return Device.FromEvidence(address, evidence, observed, observed, online);
    }

    // ------------------------------------------------------------ слияние свидетельств

    [Fact]
    public void ManualEvidence_OutweighsEverything()
    {
        // Главное свойство инвентаря: правка человека переживает пересканирование,
        // потому что она тоже свидетельство, только с наивысшим весом.
        var device = Device.FromEvidence(
            "192.168.1.10",
            [
                Evidence.Of(EvidenceSource.Netbios, EvidenceKind.HostName, "NAS2", Evening),
                Evidence.Of(EvidenceSource.Manual, EvidenceKind.HostName, "Хранилище", Morning),
            ],
            Morning,
            Evening,
            isOnline: true);

        Assert.Equal("Хранилище", device.HostName);
    }

    [Fact]
    public void HeavierSource_WinsOverFresherOne()
    {
        // Имя из mDNS точнее имени из обратной зоны: первое устройство сообщает о себе
        // само, второе взято из записи, которую мог оставить прежний владелец адреса.
        var device = Device.FromEvidence(
            "192.168.1.10",
            [
                Evidence.Of(EvidenceSource.ReverseDns, EvidenceKind.HostName, "старое-имя", Evening),
                Evidence.Of(EvidenceSource.Mdns, EvidenceKind.HostName, "принтер", Morning),
            ],
            Morning,
            Evening,
            isOnline: true);

        Assert.Equal("принтер", device.HostName);
    }

    [Fact]
    public void EqualWeight_ResolvedByFreshness()
    {
        var device = Device.FromEvidence(
            "192.168.1.10",
            [
                Evidence.Of(EvidenceSource.ArpTable, EvidenceKind.MacAddress, "AA-AA-AA-AA-AA-AA", Morning),
                Evidence.Of(EvidenceSource.ArpTable, EvidenceKind.MacAddress, "BB-BB-BB-BB-BB-BB", Evening),
            ],
            Morning,
            Evening,
            isOnline: true);

        Assert.Equal("BB-BB-BB-BB-BB-BB", device.MacAddress);
    }

    [Fact]
    public void Merge_IsIndependentOfOrder()
    {
        // Пересчёт обязан быть детерминированным: иначе повторное сканирование меняло бы
        // инвентарь произвольно, и различия перестали бы что-либо значить.
        Evidence[] evidence =
        [
            Evidence.Of(EvidenceSource.ReverseDns, EvidenceKind.HostName, "по-dns", Evening),
            Evidence.Of(EvidenceSource.Netbios, EvidenceKind.HostName, "ПО-NETBIOS", Morning),
            Evidence.Of(EvidenceSource.Mdns, EvidenceKind.HostName, "по-mdns", Morning),
        ];

        var straight = EvidenceMerge.Resolve(evidence, EvidenceKind.HostName);
        var reversed = EvidenceMerge.Resolve(evidence.Reverse().ToList(), EvidenceKind.HostName);

        Assert.Equal(straight, reversed);
        Assert.Equal("по-mdns", straight);
    }

    [Fact]
    public void OpenPorts_AreCollectedAndSorted()
    {
        var device = Device.FromEvidence(
            "192.168.1.10",
            [
                Evidence.Of(EvidenceSource.TcpConnect, EvidenceKind.OpenPort, "445", Morning),
                Evidence.Of(EvidenceSource.TcpConnect, EvidenceKind.OpenPort, "80", Morning),
                Evidence.Of(EvidenceSource.TcpConnect, EvidenceKind.OpenPort, "445", Evening),
            ],
            Morning,
            Evening,
            isOnline: true);

        Assert.Equal(WebAndSmb, device.OpenPorts);
    }

    [Fact]
    public void Identity_PrefersMacOverAddress()
    {
        Assert.Equal("AA-BB-CC-DD-EE-FF", Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF").Identity);
        Assert.Equal("192.168.1.10", Make("192.168.1.10").Identity);
    }

    [Theory]
    [InlineData("02-00-00-00-00-01", true)]
    [InlineData("AE-73-1E-DB-0C-40", true)]
    [InlineData("00-15-5D-C8-B1-09", false)]
    [InlineData("D8:43:AE:5F:BF:B4", false)]
    [InlineData(null, false)]
    public void LocalMacAddress_IsRecognised(string? mac, bool expected) =>
        Assert.Equal(expected, Make("192.168.1.10", mac: mac).HasLocalMacAddress);

    // ------------------------------------------------------------ различия

    [Fact]
    public void DhcpAddressChange_IsAChangeAndNotADisappearance()
    {
        // Ради этого тождество опознаётся по MAC. Опознание по адресу показало бы
        // одно устройство как исчезнувшее и одновременно появившееся.
        var before = new[] { Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF", name: "НОУТБУК") };
        var after = new[] { Make("192.168.1.55", mac: "AA-BB-CC-DD-EE-FF", name: "НОУТБУК", at: Evening) };

        var diff = ScanDiff.Between(before, after);

        Assert.Empty(diff.Appeared);
        Assert.Empty(diff.Disappeared);

        var (device, changes) = Assert.Single(diff.Changed);
        Assert.Equal("192.168.1.55", device.Address);

        var change = Assert.Single(changes);
        Assert.Equal("адрес", change.Field);
    }

    [Fact]
    public void NewAndGoneDevices_AreReported()
    {
        var before = new[] { Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF") };
        var after = new[] { Make("192.168.1.20", mac: "11-22-33-44-55-66", at: Evening) };

        var diff = ScanDiff.Between(before, after);

        Assert.Equal("192.168.1.20", Assert.Single(diff.Appeared).Address);
        Assert.Equal("192.168.1.10", Assert.Single(diff.Disappeared).Address);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void DeviceWithSeveralAddresses_IsNotReportedEveryScan()
    {
        // Маршрутизаторы и гипервизоры занимают несколько адресов одним MAC. Без сведения
        // сравнение брало бы то один адрес, то другой и объявляло это сменой адреса
        // при каждом сканировании.
        Device[] both =
        [
            Make("192.168.1.4", mac: "AA-BB-CC-DD-EE-FF"),
            Make("192.168.1.5", mac: "AA-BB-CC-DD-EE-FF"),
        ];

        var diff = ScanDiff.Between(both, [.. both.Reverse()]);

        Assert.True(diff.IsEmpty, "Один и тот же набор адресов не может быть изменением.");
    }

    [Fact]
    public void LostName_IsNotAChange()
    {
        // Обратный DNS отвечает не каждый раз. Имя, которое было и не стало, —
        // почти всегда не переименование, а неответивший резолвер; показывать это
        // изменением значит утопить настоящие события в шуме.
        var before = new[] { Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF", name: "СЕРВЕР") };
        var after = new[] { Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF", at: Evening) };

        Assert.True(ScanDiff.Between(before, after).IsEmpty);
    }

    [Fact]
    public void RenamedDevice_IsAChange()
    {
        var before = new[] { Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF", name: "СЕРВЕР") };
        var after = new[] { Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF", name: "СЕРВЕР-2", at: Evening) };

        var (_, changes) = Assert.Single(ScanDiff.Between(before, after).Changed);

        Assert.Equal("имя", Assert.Single(changes).Field);
    }

    [Fact]
    public void GoneOffline_IsAChangeNotADisappearance()
    {
        // Устройство, которое перестало отвечать, но осталось в таблице ARP, никуда
        // не делось — оно молчит. Это разные события, и путать их нельзя.
        var before = new[] { Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF") };
        var after = new[] { Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF", online: false, at: Evening) };

        var diff = ScanDiff.Between(before, after);

        Assert.Empty(diff.Disappeared);

        var (_, changes) = Assert.Single(diff.Changed);
        Assert.Equal("доступность", Assert.Single(changes).Field);
    }

    [Fact]
    public void MergedDevice_KeepsAllItsAddresses()
    {
        // Маршрутизатор с двумя адресами — одно устройство, но оба адреса должны
        // остаться на виду: иначе инвентарь молча теряет часть найденного.
        Device[] both =
        [
            Make("192.168.1.4", mac: "AA-BB-CC-DD-EE-FF"),
            Make("192.168.1.5", mac: "AA-BB-CC-DD-EE-FF"),
        ];

        var appeared = Assert.Single(ScanDiff.Between([], both).Appeared);

        Assert.Equal(TwoAddresses, appeared.Addresses);
        Assert.Equal(SecondAddressOnly, appeared.ExtraAddresses);
    }

    [Fact]
    public void SingleAddressDevice_HasNoExtras()
    {
        var device = Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF");

        Assert.Equal(OneAddress, device.Addresses);
        Assert.Empty(device.ExtraAddresses);
    }

    [Fact]
    public void VendorDisplay_ExplainsWhatTheRegistryCannot()
    {
        // Три случая, и во всех трёх столбец «вендор» должен говорить по делу.
        Assert.Contains(
            "VRRP",
            Make("192.168.1.1", mac: "00-00-5E-00-01-C8", vendor: "ICANN, IANA Department").VendorDisplay,
            StringComparison.Ordinal);

        Assert.Equal("локальный MAC", Make("192.168.1.92", mac: "2E-AF-19-F1-AF-E1").VendorDisplay);
        Assert.Equal("Synology", Make("192.168.1.251", mac: "00-11-32-E4-70-AA", vendor: "Synology").VendorDisplay);
    }

    [Fact]
    public void IdenticalScans_ProduceNoDiff() =>
        Assert.True(ScanDiff.Between(
            [Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF", name: "СЕРВЕР", vendor: "Synology")],
            [Make("192.168.1.10", mac: "AA-BB-CC-DD-EE-FF", name: "СЕРВЕР", vendor: "Synology")]).IsEmpty);

    [Fact]
    public void EmptyScans_ProduceNoDiff() => Assert.True(ScanDiff.Between([], []).IsEmpty);
}
