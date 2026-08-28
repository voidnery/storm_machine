using StormMachine.Domain.Snmp;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Учётные данные, счётчики и роль устройства.
/// </summary>
/// <remarks>
/// Главное, что здесь закрепляется: <b>продукт не выдаёт правдоподобное число
/// за измерение</b>. Счётчик, который переполнился или начался заново после
/// перезагрузки, не даёт разницы — и молча вернуть на его месте что-нибудь
/// похожее на правду было бы худшим из возможных поведений: ошибку в разы
/// никто не заметит.
/// </remarks>
public sealed class SnmpTests
{
    private static SnmpCredential Credential(SnmpVersion version, string? community = "public") => new()
    {
        Id = Guid.NewGuid(),
        Name = "набор",
        Version = version,
        Community = community,
    };

    // ------------------------------------------------------------- учётные данные

    [Fact(DisplayName = "Строка сообщества защитой не считается")]
    public void CommunityIsNotProtection()
    {
        // Она идёт по сети открытым текстом. Назвать её защитой значило бы обмануть
        // того, кто на основании этого решает, можно ли опрашивать через транзит.
        var credential = Credential(SnmpVersion.V2c);

        Assert.False(credential.IsProtected);
        Assert.Equal("noAuthNoPriv", credential.SecurityLevel);
        Assert.Contains(credential.Warnings(), w => w.Contains("открытым текстом", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Первая версия предупреждает о 32-разрядных счётчиках")]
    public void V1WarnsAboutCounters()
    {
        var credential = Credential(SnmpVersion.V1);

        Assert.False(credential.HasHighCapacityCounters);
        Assert.Contains(credential.Warnings(), w => w.Contains("34 секунды", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Шифрование без проверки подлинности отвергается")]
    public void PrivacyRequiresAuthentication()
    {
        // RFC 3414 §1.4: получатель, не знающий, от кого сообщение, не может
        // доверять и его содержимому.
        var credential = Credential(SnmpVersion.V3, community: null) with
        {
            UserName = "storm",
            AuthProtocol = SnmpAuthProtocol.None,
            PrivacyProtocol = SnmpPrivacyProtocol.Aes128,
            PrivacyPassword = "секрет",
        };

        Assert.Contains(credential.Validate(), e => e.Contains("RFC 3414", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "AES-256 поверх MD5 отвергается как бессмысленный")]
    public void Aes256WithMd5IsRefused()
    {
        var credential = Credential(SnmpVersion.V3, community: null) with
        {
            UserName = "storm",
            AuthProtocol = SnmpAuthProtocol.Md5,
            AuthPassword = "пароль",
            PrivacyProtocol = SnmpPrivacyProtocol.Aes256,
            PrivacyPassword = "секрет",
        };

        Assert.Contains(credential.Validate(), e => e.Contains("128-разрядного", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Полный набор v3 проходит проверку и считается защищённым")]
    public void FullV3IsValid()
    {
        var credential = Credential(SnmpVersion.V3, community: null) with
        {
            UserName = "storm",
            AuthProtocol = SnmpAuthProtocol.Sha256,
            AuthPassword = "пароль",
            PrivacyProtocol = SnmpPrivacyProtocol.Aes128,
            PrivacyPassword = "секрет",
        };

        Assert.Empty(credential.Validate());
        Assert.True(credential.IsProtected);
        Assert.Equal("authPriv", credential.SecurityLevel);
    }

    // ------------------------------------------------------------------ счётчики

    private static InterfaceCounters Counters(
        double seconds,
        long octets,
        long packets = 0,
        long errors = 0,
        bool high = true,
        double upSeconds = 10_000) => new()
    {
        Index = 1,
        AtUtc = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds),
        SysUpTime = TimeSpan.FromSeconds(upSeconds + seconds),
        AreHighCapacity = high,
        InOctets = octets,
        OutOctets = octets / 2,
        InPackets = packets,
        OutPackets = packets / 2,
        InErrors = errors,
    };

    [Fact(DisplayName = "Разница двух снимков даёт скорость и загрузку")]
    public void LoadIsComputed()
    {
        // 12.5 МБ за 10 секунд — это 10 Мбит/с, то есть 1% гигабитного порта.
        var load = InterfaceLoadCalculator.Between(
            Counters(0, 0),
            Counters(10, 12_500_000),
            1_000_000_000,
            out var refusal);

        Assert.Equal(LoadRefusal.None, refusal);
        Assert.NotNull(load);
        Assert.Equal(10_000_000, load!.InBitsPerSecond, 0);
        Assert.Equal(1.0, load.InPercent!.Value, 2);
    }

    [Fact(DisplayName = "Перезагрузка между снимками отменяет измерение")]
    public void RebootRefusesLoad()
    {
        // Счётчики начались заново: разница между «до» и «после» означала бы
        // не трафик, а расстояние до перезагрузки.
        var before = Counters(0, 900_000_000, upSeconds: 100_000);
        var after = Counters(10, 1_000_000, upSeconds: 5);

        Assert.Null(InterfaceLoadCalculator.Between(before, after, 1_000_000_000, out var refusal));
        Assert.Equal(LoadRefusal.Rebooted, refusal);
    }

    [Fact(DisplayName = "Счётчик, пошедший назад, отменяет измерение")]
    public void WrapRefusesLoad()
    {
        // Поправить нельзя: сколько раз счётчик обернулся, в снимке не написано.
        Assert.Null(InterfaceLoadCalculator.Between(
            Counters(0, 4_000_000_000),
            Counters(10, 100_000),
            1_000_000_000,
            out var refusal));

        Assert.Equal(LoadRefusal.Wrapped, refusal);
    }

    [Fact(DisplayName = "Снимки в обратном порядке отменяют измерение")]
    public void BadIntervalRefusesLoad()
    {
        Assert.Null(InterfaceLoadCalculator.Between(
            Counters(10, 100),
            Counters(0, 200),
            1_000_000_000,
            out var refusal));

        Assert.Equal(LoadRefusal.BadInterval, refusal);
    }

    [Fact(DisplayName = "32-разрядный счётчик на гигабите оборачивается за 34 секунды")]
    public void WrapHorizonIsThirtyFourSeconds()
    {
        // Отсюда всё ограничение на промежуток опроса. Число не круглое и не выдумано:
        // 2^32 байт по восемь бит на гигабите.
        var horizon = InterfaceLoadCalculator.WrapHorizon(1_000_000_000);

        Assert.NotNull(horizon);
        Assert.Equal(34.4, horizon!.Value.TotalSeconds, 1);
    }

    [Fact(DisplayName = "Опрос реже половины оборота признаётся негодным")]
    public void IntervalMustBeTwiceTheWrap()
    {
        // Если между опросами умещается один оборот, то умещается и два,
        // а различить их нечем.
        Assert.False(InterfaceLoadCalculator.IsIntervalSafe(TimeSpan.FromSeconds(30), 1_000_000_000, false));
        Assert.True(InterfaceLoadCalculator.IsIntervalSafe(TimeSpan.FromSeconds(15), 1_000_000_000, false));

        // 64 разряда на той же скорости оборачиваются за столетия.
        Assert.True(InterfaceLoadCalculator.IsIntervalSafe(TimeSpan.FromHours(1), 1_000_000_000, true));
    }

    [Fact(DisplayName = "Ошибки считаются долей, а не штуками")]
    public void ErrorsAreAShare()
    {
        // Сто ошибок на десять миллионов кадров и сто ошибок на тысячу — разные
        // события, и различать их обязан инструмент.
        var load = InterfaceLoadCalculator.Between(
            Counters(0, 0),
            Counters(10, 1_000_000, packets: 1_000_000, errors: 100),
            1_000_000_000,
            out _);

        Assert.NotNull(load);
        Assert.Equal(100, load!.InErrorsPerMillion!.Value, 0);
    }

    [Fact(DisplayName = "Загрузка выше сотни процентов помечается невозможной")]
    public void ImplausibleLoadIsMarked()
    {
        // Так не бывает: врёт либо заявленная скорость порта, либо счётчики.
        var load = InterfaceLoadCalculator.Between(
            Counters(0, 0),
            Counters(1, 100_000_000),
            10_000_000,
            out _);

        Assert.True(load!.IsImplausible);
    }

    // ---------------------------------------------------------------- роль и порты

    [Fact(DisplayName = "Таблица пересылки весит больше заявленных услуг")]
    public void ForwardingTableDecidesRole()
    {
        // sysServices пишет производитель как придётся; таблица пересылки
        // означает, что устройство действительно коммутирует кадры.
        var system = new SnmpSystem { Description = "нечто", Services = 0 };

        Assert.Equal(SnmpDeviceRole.Switch, system.Role(hasForwardingTable: true));
        Assert.Equal(SnmpDeviceRole.Host, system.Role(hasForwardingTable: false));
    }

    [Fact(DisplayName = "Маршрутизация вместе с коммутацией даёт третий уровень")]
    public void RoutingAndBridgingIsLayerThree()
    {
        var system = new SnmpSystem { Description = "нечто", Services = 6 };

        Assert.Equal(SnmpDeviceRole.LayerThreeSwitch, system.Role(hasForwardingTable: true));
        Assert.Equal(SnmpDeviceRole.Router, system.Role(hasForwardingTable: false));
    }

    [Fact(DisplayName = "Включённый порт без линка отличается от выключенного")]
    public void DarkPortIsNotShutdown()
    {
        // Первое — повод посмотреть, второе — чьё-то решение. Смешивать их значит
        // отправить человека проверять исправный провод.
        var dark = Port(InterfaceStatus.Up, InterfaceStatus.Down);
        var off = Port(InterfaceStatus.Down, InterfaceStatus.Down);

        Assert.True(dark.IsDark);
        Assert.False(dark.IsShutdown);
        Assert.False(off.IsDark);
        Assert.True(off.IsShutdown);
        Assert.Equal("выключен администратором", off.DescribeStatus());
    }

    [Fact(DisplayName = "Порт с одним адресом — конечное устройство, с многими — аплинк")]
    public void SolePortMeansEndpoint()
    {
        // За одним портом не бывает десяти компьютеров, если за ним не стоит
        // ещё один коммутатор.
        var single = new SwitchPort
        {
            Interface = Port(InterfaceStatus.Up, InterfaceStatus.Up),
            Addresses = [Entry("AA-BB-CC-00-00-01")],
        };

        var many = new SwitchPort
        {
            Interface = Port(InterfaceStatus.Up, InterfaceStatus.Up),
            Addresses =
            [
                Entry("AA-BB-CC-00-00-01"),
                Entry("AA-BB-CC-00-00-02"),
                Entry("AA-BB-CC-00-00-03"),
                Entry("AA-BB-CC-00-00-04"),
            ],
        };

        Assert.Equal("AA-BB-CC-00-00-01", single.SoleAddress);
        Assert.False(single.LooksLikeUplink);
        Assert.Null(many.SoleAddress);
        Assert.True(many.LooksLikeUplink);
    }

    [Fact(DisplayName = "Скорость выше 4 Гбит/с показывается человеческим языком")]
    public void SpeedIsDescribed()
    {
        Assert.Equal("10 Гбит/с", (Port(InterfaceStatus.Up, InterfaceStatus.Up) with
        {
            SpeedBitsPerSecond = 10_000_000_000,
        }).DescribeSpeed());

        Assert.Equal("скорость неизвестна", (Port(InterfaceStatus.Up, InterfaceStatus.Up) with
        {
            SpeedBitsPerSecond = 0,
        }).DescribeSpeed());
    }

    private static SnmpInterface Port(InterfaceStatus admin, InterfaceStatus oper) => new()
    {
        Index = 1,
        Name = "Gi0/1",
        Type = SnmpInterface.EthernetType,
        SpeedBitsPerSecond = 1_000_000_000,
        AdminStatus = admin,
        OperStatus = oper,
    };

    private static ForwardingEntry Entry(string mac) => new()
    {
        MacAddress = mac,
        BridgePort = 1,
        IfIndex = 1,
    };
}
