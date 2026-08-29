using Lextm.SharpSnmpLib;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;
using SnmpException = StormMachine.Application.Abstractions.SnmpException;

namespace StormMachine.Snmp.UnitTests;

/// <summary>
/// Разбор ответов оборудования.
/// </summary>
/// <remarks>
/// Таблица в SNMP — это плоский список узлов, где номер строки дописан к идентификатору
/// столбца, и весь разбор состоит из соглашений о том, как этот номер устроен.
/// У портов он одно число, у соседей LLDP — тройка, у таблицы пересылки — шесть байт
/// адреса, а с VLAN ещё и её номер впереди. Ошибиться в любом из них можно только
/// на настоящих байтах — отсюда дублёр вместо подмены объектов.
/// </remarks>
public sealed class SharpSnmpClientTests
{
    private static SnmpCredential Credential(int port) => new()
    {
        Id = Guid.NewGuid(),
        Name = "стенд",
        Version = SnmpVersion.V2c,
        Community = "public",
        Port = port,
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
    };

    private static void Port(
        Dictionary<string, ISnmpData> mib,
        int index,
        string descr,
        string? name = null,
        string? alias = null,
        long megabits = 1000,
        int admin = 1,
        int oper = 1)
    {
        var i = index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        mib[$"1.3.6.1.2.1.2.2.1.2.{i}"] = FakeAgent.Text(descr);
        mib[$"1.3.6.1.2.1.2.2.1.3.{i}"] = new Integer32(6);
        mib[$"1.3.6.1.2.1.2.2.1.5.{i}"] = new Gauge32(1_000_000_000);
        mib[$"1.3.6.1.2.1.2.2.1.7.{i}"] = new Integer32(admin);
        mib[$"1.3.6.1.2.1.2.2.1.8.{i}"] = new Integer32(oper);

        if (name is not null)
        {
            mib[$"1.3.6.1.2.1.31.1.1.1.1.{i}"] = FakeAgent.Text(name);
            mib[$"1.3.6.1.2.1.31.1.1.1.15.{i}"] = new Gauge32((uint)megabits);
        }

        if (alias is not null)
        {
            mib[$"1.3.6.1.2.1.31.1.1.1.18.{i}"] = FakeAgent.Text(alias);
        }
    }

    // ------------------------------------------------------------------ системная группа

    [Fact(DisplayName = "Системная группа читается одним запросом")]
    public async Task SystemIsReadInOneRequest()
    {
        // Семь отдельных запросов стоили бы семи кругов по сети, а на объекте
        // через узкий канал это заметно.
        await using var agent = new FakeAgent(FakeAgent.System());

        var client = new SharpSnmpClient();
        var system = await client.GetSystemAsync("127.0.0.1", Credential(agent.Port));

        Assert.NotNull(system);
        Assert.Equal("sw-test", system!.Name);
        Assert.Equal("Fake switch", system.ShortDescription);
        Assert.Equal(TimeSpan.FromSeconds(3600), system.UpTime);
        Assert.Equal(1, agent.Requests);
    }

    [Fact(DisplayName = "Кириллица в описании не превращается в вопросительные знаки")]
    public async Task CyrillicSurvives()
    {
        // sysLocation и подписи портов на объектах пишут по-русски, а библиотека
        // по умолчанию разбирает строки как ASCII. RFC 3411 §5 предписывает UTF-8.
        await using var agent = new FakeAgent(FakeAgent.System());

        var system = await new SharpSnmpClient().GetSystemAsync("127.0.0.1", Credential(agent.Port));

        Assert.Equal("серверная, стойка 2", system!.Location);
    }

    [Fact(DisplayName = "Устройство без системной группы возвращает пустоту, а не ошибку")]
    public async Task MissingSystemIsNull()
    {
        await using var agent = new FakeAgent(new Dictionary<string, ISnmpData>(StringComparer.Ordinal)
        {
            ["1.3.6.1.2.1.99.1.0"] = FakeAgent.Text("нечто постороннее"),
        });

        Assert.Null(await new SharpSnmpClient().GetSystemAsync("127.0.0.1", Credential(agent.Port)));
    }

    // ------------------------------------------------------------------------- порты

    [Fact(DisplayName = "Порты собираются из основной и расширенной таблиц")]
    public async Task InterfacesAreJoined()
    {
        var mib = FakeAgent.System();

        Port(mib, 1, "GigabitEthernet0/1", "Gi0/1", "к ядру");
        Port(mib, 2, "GigabitEthernet0/2", "Gi0/2", "серверная", oper: 2);

        await using var agent = new FakeAgent(mib);

        var ports = await new SharpSnmpClient().GetInterfacesAsync("127.0.0.1", Credential(agent.Port));

        Assert.Equal(2, ports.Count);

        // Короткое имя предпочитается описанию: оно совпадает с тем, что видно
        // в консоли устройства.
        Assert.Equal("Gi0/1", ports[0].Name);
        Assert.Equal("GigabitEthernet0/1", ports[0].Description);
        Assert.Equal("к ядру", ports[0].Alias);
        Assert.Equal(1_000_000_000, ports[0].SpeedBitsPerSecond);

        Assert.True(ports[1].IsDark);
    }

    [Fact(DisplayName = "Порядок портов не зависит от порядка ответов")]
    public async Task InterfacesAreOrdered()
    {
        var mib = FakeAgent.System();

        Port(mib, 14, "GigabitEthernet0/14", "Gi0/14");
        Port(mib, 2, "GigabitEthernet0/2", "Gi0/2");

        await using var agent = new FakeAgent(mib);

        var ports = await new SharpSnmpClient().GetInterfacesAsync("127.0.0.1", Credential(agent.Port));

        Assert.Equal([2, 14], ports.Select(p => p.Index));
    }

    [Fact(DisplayName = "Без расширенной таблицы порт читается из основной")]
    public async Task WorksWithoutExtendedTable()
    {
        // Первая версия протокола её не знает, простые устройства не реализуют.
        // Отсутствие — не отказ.
        var mib = FakeAgent.System();

        Port(mib, 1, "GigabitEthernet0/1");

        await using var agent = new FakeAgent(mib);

        var ports = await new SharpSnmpClient().GetInterfacesAsync("127.0.0.1", Credential(agent.Port));

        Assert.Single(ports);
        Assert.Equal("GigabitEthernet0/1", ports[0].Name);
        Assert.Null(ports[0].Alias);
    }

    [Fact(DisplayName = "Скорость выше 4 Гбит/с берётся из расширенной таблицы")]
    public async Task HighSpeedWins()
    {
        // ifSpeed 32-разрядный и упирается в 4.29 Гбит/с: десятигигабитный порт
        // по нему неотличим от четырёхгигабитного.
        var mib = FakeAgent.System();

        Port(mib, 1, "TenGigE0/1", "Te0/1", megabits: 10_000);
        mib["1.3.6.1.2.1.2.2.1.5.1"] = new Gauge32(4_294_967_295);

        await using var agent = new FakeAgent(mib);

        var ports = await new SharpSnmpClient().GetInterfacesAsync("127.0.0.1", Credential(agent.Port));

        Assert.Equal(10_000_000_000, ports[0].SpeedBitsPerSecond);
    }

    // ---------------------------------------------------------------------- счётчики

    [Fact(DisplayName = "64-разрядные счётчики предпочитаются 32-разрядным")]
    public async Task HighCapacityCountersWin()
    {
        var mib = FakeAgent.System();

        Port(mib, 1, "GigabitEthernet0/1", "Gi0/1");

        mib["1.3.6.1.2.1.2.2.1.10.1"] = new Counter32(1_000);
        mib["1.3.6.1.2.1.31.1.1.1.6.1"] = new Counter64(9_000_000_000);
        mib["1.3.6.1.2.1.31.1.1.1.10.1"] = new Counter64(4_000_000_000);

        await using var agent = new FakeAgent(mib);

        var counters = await new SharpSnmpClient().GetCountersAsync("127.0.0.1", Credential(agent.Port));

        Assert.Single(counters);
        Assert.True(counters[0].AreHighCapacity);
        Assert.Equal(9_000_000_000, counters[0].InOctets);
    }

    [Fact(DisplayName = "Без 64-разрядных счётчиков берутся 32-разрядные с пометкой")]
    public async Task FallsBackToNarrowCounters()
    {
        // Пометка существеннее самих чисел: от неё зависит, можно ли вообще
        // считать разницу при выбранной паузе.
        var mib = FakeAgent.System();

        Port(mib, 1, "GigabitEthernet0/1", "Gi0/1");

        mib["1.3.6.1.2.1.2.2.1.10.1"] = new Counter32(1_000);
        mib["1.3.6.1.2.1.2.2.1.16.1"] = new Counter32(500);

        await using var agent = new FakeAgent(mib);

        var counters = await new SharpSnmpClient().GetCountersAsync("127.0.0.1", Credential(agent.Port));

        Assert.Single(counters);
        Assert.False(counters[0].AreHighCapacity);
        Assert.Equal(1_000, counters[0].InOctets);
    }

    // ------------------------------------------------------------------------ соседи

    [Fact(DisplayName = "Сосед LLDP разбирается вместе с номером нашего порта")]
    public async Task LldpNeighborIsParsed()
    {
        // Индекс тройной: отметка времени, локальный порт, номер соседа.
        // Нужен средний — он и есть наш порт.
        var mib = FakeAgent.System();

        mib["1.0.8802.1.1.2.1.4.1.1.5.0.3.1"] = FakeAgent.Text("00-1C-0E-AA-BB-01");
        mib["1.0.8802.1.1.2.1.4.1.1.7.0.3.1"] = FakeAgent.Text("Te1/0/24");
        mib["1.0.8802.1.1.2.1.4.1.1.8.0.3.1"] = FakeAgent.Text("к доступу");
        mib["1.0.8802.1.1.2.1.4.1.1.9.0.3.1"] = FakeAgent.Text("sw-core-01");

        await using var agent = new FakeAgent(mib);

        var neighbors = await new SharpSnmpClient().GetNeighborsAsync("127.0.0.1", Credential(agent.Port));

        Assert.Single(neighbors);
        Assert.Equal(3, neighbors[0].LocalIfIndex);
        Assert.Equal("sw-core-01", neighbors[0].RemoteName);
        Assert.Equal("Te1/0/24", neighbors[0].RemotePort);
        Assert.Equal(NeighborProtocol.Lldp, neighbors[0].Protocol);
    }

    [Fact(DisplayName = "Без LLDP читается CDP")]
    public async Task CdpIsReadWhenLldpIsSilent()
    {
        // Там, где LLDP выключен, у Cisco остаётся своя ветка. Читать обе разом
        // нельзя: соседи задвоились бы, а различить их снаружи нечем.
        var mib = FakeAgent.System();

        mib["1.3.6.1.4.1.9.9.23.1.2.1.1.6.5.1"] = FakeAgent.Text("sw-core-01");
        mib["1.3.6.1.4.1.9.9.23.1.2.1.1.7.5.1"] = FakeAgent.Text("GigabitEthernet1/0/1");

        await using var agent = new FakeAgent(mib);

        var neighbors = await new SharpSnmpClient().GetNeighborsAsync("127.0.0.1", Credential(agent.Port));

        Assert.Single(neighbors);
        Assert.Equal(NeighborProtocol.Cdp, neighbors[0].Protocol);
        Assert.Equal(5, neighbors[0].LocalIfIndex);
    }

    [Fact(DisplayName = "Устройство без соседей отдаёт пустой список, а не ошибку")]
    public async Task NoNeighborsIsNotAnError()
    {
        await using var agent = new FakeAgent(FakeAgent.System());

        Assert.Empty(await new SharpSnmpClient().GetNeighborsAsync("127.0.0.1", Credential(agent.Port)));
    }

    // -------------------------------------------------------------- таблица пересылки

    [Fact(DisplayName = "Адрес и порт достаются из таблицы пересылки")]
    public async Task ForwardingIsParsed()
    {
        var mib = FakeAgent.System();
        var mac = FakeAgent.MacKey("A4-BB-6D-11-22-33");

        // Номер порта моста не равен ifIndex: их связывает отдельная таблица.
        mib["1.3.6.1.2.1.17.1.4.1.2.7"] = new Integer32(21);
        mib[$"1.3.6.1.2.1.17.4.3.1.2.{mac}"] = new Integer32(7);
        mib[$"1.3.6.1.2.1.17.4.3.1.3.{mac}"] = new Integer32(3);

        await using var agent = new FakeAgent(mib);

        var entries = await new SharpSnmpClient().GetForwardingAsync("127.0.0.1", Credential(agent.Port));

        Assert.Single(entries);
        Assert.Equal("A4-BB-6D-11-22-33", entries[0].MacAddress);
        Assert.Equal(7, entries[0].BridgePort);
        Assert.Equal(21, entries[0].IfIndex);
        Assert.True(entries[0].IsLearned);
    }

    [Fact(DisplayName = "Собственный адрес моста выученным не считается")]
    public async Task OwnAddressIsNotLearned()
    {
        // Состояние 4 — собственный адрес моста. Он не означает, что в порт
        // что-то воткнуто.
        var mib = FakeAgent.System();
        var mac = FakeAgent.MacKey("00-50-56-00-00-01");

        mib[$"1.3.6.1.2.1.17.4.3.1.2.{mac}"] = new Integer32(1);
        mib[$"1.3.6.1.2.1.17.4.3.1.3.{mac}"] = new Integer32(4);

        await using var agent = new FakeAgent(mib);

        var entries = await new SharpSnmpClient().GetForwardingAsync("127.0.0.1", Credential(agent.Port));

        Assert.Single(entries);
        Assert.False(entries[0].IsLearned);
    }

    [Fact(DisplayName = "Таблица с VLAN читается, когда обычной нет")]
    public async Task VlanAwareTableIsRead()
    {
        // Устройства с VLAN держат таблицу в другой ветке, а старую отдают пустой.
        // Адрес там стоит после номера VLAN — брать первые шесть частей было бы
        // ошибкой ровно на тех устройствах, ради которых вторая таблица и читается.
        var mib = FakeAgent.System();

        mib[$"1.3.6.1.2.1.17.7.1.2.2.1.2.100.{FakeAgent.MacKey("B8-27-EB-AA-BB-CC")}"] = new Integer32(9);

        await using var agent = new FakeAgent(mib);

        var entries = await new SharpSnmpClient().GetForwardingAsync("127.0.0.1", Credential(agent.Port));

        Assert.Single(entries);
        Assert.Equal("B8-27-EB-AA-BB-CC", entries[0].MacAddress);
        Assert.Equal(100, entries[0].Vlan);
    }

    // ---------------------------------------------------------------------- отказы

    [Fact(DisplayName = "Молчащее устройство даёт внятный отказ, а не зависание")]
    public async Task SilenceIsExplained()
    {
        // Порт, на котором никого нет: ответа не будет никогда.
        var credential = Credential(59_999) with { Timeout = TimeSpan.FromMilliseconds(300), Retries = 0 };

        var error = await Assert.ThrowsAsync<SnmpException>(
            () => new SharpSnmpClient().GetSystemAsync("127.0.0.1", credential));

        Assert.Equal(SnmpFailure.NoAnswer, error.Reason);
        Assert.Contains("SNMP выключен", error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Несуществующее имя узла называется своей причиной")]
    public async Task UnknownHostIsNamed()
    {
        var error = await Assert.ThrowsAsync<SnmpException>(
            () => new SharpSnmpClient().GetSystemAsync(
                "нет-такого-узла.invalid",
                Credential(161) with { Timeout = TimeSpan.FromMilliseconds(300), Retries = 0 }));

        Assert.Equal(SnmpFailure.UnknownHost, error.Reason);
    }

    [Fact(DisplayName = "Обход произвольной ветки ограничен заданным пределом")]
    public async Task WalkRespectsLimit()
    {
        // Таблица пересылки крупного коммутатора — десятки тысяч записей.
        var mib = FakeAgent.System();

        for (var i = 1; i <= 20; i++)
        {
            mib[$"1.3.6.1.2.1.2.2.1.2.{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"] =
                FakeAgent.Text($"порт {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        await using var agent = new FakeAgent(mib);

        var found = await new SharpSnmpClient()
            .WalkAsync("127.0.0.1", Credential(agent.Port), "1.3.6.1.2.1.2.2.1.2", limit: 5);

        Assert.Equal(5, found.Count);
    }
}
