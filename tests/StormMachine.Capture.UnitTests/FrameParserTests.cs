using StormMachine.Capture.Npcap;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;

namespace StormMachine.Capture.UnitTests;

/// <summary>
/// Разбор кадров.
/// </summary>
/// <remarks>
/// Ради этих проверок разбор и отделён от драйвера. Драйвер захвата есть далеко
/// не у всех — на машине разработки его нет вовсе, — а ошибиться в смещении TLV
/// или в порядке байтов можно на любой машине, и увидеть такую ошибку в живом эфире
/// почти невозможно: она даёт не отказ, а правдоподобный мусор.
/// </remarks>
public sealed class FrameParserTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static FrameFinding Parse(byte[] frame) =>
        FrameParser.Parse(frame, When, "Ethernet", localIfIndex: 7)
        ?? throw new InvalidOperationException("кадр не разобран");

    // -------------------------------------------------------------------------- LLDP

    [Fact(DisplayName = "Сосед по LLDP разбирается со всеми полями")]
    public void LldpIsParsed()
    {
        var neighbor = Parse(Frames.Lldp()).Neighbor;

        Assert.NotNull(neighbor);
        Assert.Equal(NeighborProtocol.Lldp, neighbor!.Protocol);
        Assert.Equal("sw-core-01", neighbor.RemoteName);
        Assert.Equal("Te1/0/24", neighbor.RemotePort);
        Assert.Equal("Core switch, firmware 4.2", neighbor.RemoteDescription);
        Assert.Equal("00-1C-0E-AA-BB-01", neighbor.RemoteChassisId);
    }

    [Fact(DisplayName = "Услышанный сосед помечен захватом, а не опросом")]
    public void CapturedNeighborIsMarked()
    {
        // Различие не косметическое: опрос по SNMP отвечает за все порты коммутатора,
        // захват — только за тот, куда воткнуты мы. Путать их на карте нельзя.
        var neighbor = Parse(Frames.Lldp()).Neighbor!;

        Assert.Equal(NeighborSource.Capture, neighbor.Source);
        Assert.Contains("услышан своим адаптером", neighbor.Because, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Наш конец связи берётся от адаптера, а не из кадра")]
    public void LocalEndComesFromAdapter()
    {
        // В кадре нашего порта нет и быть не может: кадр пришёл к нам, а не рассказывает
        // про нас. Единственный источник — тот адаптер, которым слушали.
        var neighbor = Parse(Frames.Lldp()).Neighbor!;

        Assert.Equal("Ethernet", neighbor.LocalPort);
        Assert.Equal(7, neighbor.LocalIfIndex);
    }

    [Fact(DisplayName = "Кириллица в подписи порта не портится")]
    public void CyrillicSurvives()
    {
        // Подписи портов на объектах пишут по-русски, и байты в кадре — UTF-8.
        var neighbor = Parse(Frames.Lldp()).Neighbor!;

        Assert.Equal("к доступу, стойка 2", neighbor.RemotePortDescription);
    }

    [Fact(DisplayName = "Сосед без описаний разбирается по обязательным полям")]
    public void MinimalLldpIsEnough()
    {
        // Обязательны только шасси, порт и время жизни. Остальное объявляют не все.
        var neighbor = Parse(Frames.Lldp(portDescription: null, systemDescription: null)).Neighbor!;

        Assert.Equal("sw-core-01", neighbor.RemoteName);
        Assert.Null(neighbor.RemotePortDescription);
        Assert.Null(neighbor.RemoteDescription);
    }

    // --------------------------------------------------------------------------- CDP

    [Fact(DisplayName = "Сосед по CDP разбирается сквозь заголовок SNAP")]
    public void CdpIsParsed()
    {
        // CDP идёт не поверх EtherType, а внутри LLC/SNAP. Пропустить восемь байт
        // заголовка — значит прочитать TLV со сдвигом и получить правдоподобный мусор.
        var neighbor = Parse(Frames.Cdp()).Neighbor;

        Assert.NotNull(neighbor);
        Assert.Equal(NeighborProtocol.Cdp, neighbor!.Protocol);
        Assert.Equal("sw-access-02", neighbor.RemoteName);
        Assert.Equal("GigabitEthernet1/0/1", neighbor.RemotePort);
        Assert.Equal("cisco WS-C2960X", neighbor.RemoteDescription);
    }

    [Fact(DisplayName = "Кадр не на групповой адрес CDP соседом не считается")]
    public void ForeignFrameIsNotCdp()
    {
        var frame = Frames.Cdp();

        // Портим адрес назначения: остальное в кадре остаётся правильным.
        frame[0] = 0x02;

        Assert.Null(FrameParser.Parse(frame, When, "Ethernet"));
    }

    // -------------------------------------------------------------------------- DHCP

    [Fact(DisplayName = "Ответ DHCP разбирается с адресом сервера и объявленным шлюзом")]
    public void DhcpIsParsed()
    {
        var sighting = Parse(Frames.Dhcp()).Dhcp;

        Assert.NotNull(sighting);
        Assert.Equal("192.168.1.1", sighting!.ServerAddress);
        Assert.Equal("192.168.1.50", sighting.OfferedAddress);
        Assert.Equal("192.168.1.1", sighting.OfferedGateway);
        Assert.Equal(DhcpMessage.Offer, sighting.Message);
        Assert.Equal("00-1C-0E-AA-BB-01", sighting.ServerMac);
    }

    [Fact(DisplayName = "Адрес сервера берётся из опции 54, а не из отправителя")]
    public void ServerComesFromOption()
    {
        // Отправителем может быть агент ретрансляции: он пересылает чужие ответы
        // и сервером не является. Спутать их — значит обвинить не того.
        var sighting = Parse(Frames.Dhcp(serverAddress: "10.0.0.5")).Dhcp!;

        Assert.Equal("10.0.0.5", sighting.ServerAddress);
    }

    [Fact(DisplayName = "Подтверждение отличается от предложения")]
    public void AckIsDistinct()
    {
        var sighting = Parse(Frames.Dhcp(messageType: 5)).Dhcp!;

        Assert.Equal(DhcpMessage.Ack, sighting.Message);
    }

    // ------------------------------------------------------------------- прочие кадры

    [Fact(DisplayName = "Посторонний кадр не разбирается и не роняет разбор")]
    public void UnknownFrameIsSkipped()
    {
        // Фильтр драйвера пропускает лишнее, и это норма: разбирать всё подряд незачем.
        byte[] arp = [.. new byte[6], .. Frames.OurMac, 0x08, 0x06, .. new byte[28]];

        Assert.Null(FrameParser.Parse(arp, When, "Ethernet"));
    }

    [Fact(DisplayName = "Обрезанный кадр не роняет прослушивание")]
    public void TruncatedFrameIsSurvivable()
    {
        // В эфире битые посылки бывают. Одна такая не должна обрывать прослушивание.
        var frame = Frames.Lldp();

        Assert.Null(FrameParser.Parse(frame[..8], When, "Ethernet"));
        Assert.Null(FrameParser.Parse([], When, "Ethernet"));
    }

    [Fact(DisplayName = "Кадр с ложной длиной TLV не уводит разбор в мусор")]
    public void BadTlvLengthStopsParsing()
    {
        var frame = Frames.Cdp();

        // Длина первого TLV меньше собственного заголовка. Смещение считается так:
        // 14 байт Ethernet, 8 байт LLC/SNAP, 4 байта заголовка CDP — значит TLV
        // начинается с 26-го, его тип занимает два байта, длина — следующие два.
        frame[28] = 0x00;
        frame[29] = 0x01;

        var finding = FrameParser.Parse(frame, When, "Ethernet");

        // Разбор либо остановился без соседа, либо вернул то, что успел прочитать
        // до испорченного места, — но не упал и не выдумал полей.
        Assert.True(finding is null || finding.Neighbor?.RemotePort is null);
    }

    // ------------------------------------------------------------------ итог по DHCP

    [Fact(DisplayName = "Два сервера DHCP — повод разобраться, но не вердикт")]
    public void TwoServersNeedAttention()
    {
        // Отказоустойчивая пара DHCP в одном домене — обычное дело. Объявить её
        // подставной значило бы поднять ложную тревогу там, где всё правильно.
        var finding = new DhcpFinding
        {
            Sightings =
            [
                Parse(Frames.Dhcp(serverAddress: "192.168.1.1")).Dhcp!,
                Parse(Frames.Dhcp(serverAddress: "192.168.1.2")).Dhcp!,
            ],
        };

        Assert.Equal(2, finding.ServerCount);
        Assert.True(finding.NeedsAttention);
    }

    [Fact(DisplayName = "Чужой объявленный шлюз — то единственное, что продукт утверждает сам")]
    public void ForeignGatewayIsNamed()
    {
        // Посторонний сервер обычно выдаёт себя же шлюзом и уводит через себя весь
        // трафик клиента. Это проверяемое утверждение, а не догадка о намерениях.
        var finding = new DhcpFinding
        {
            Sightings =
            [
                Parse(Frames.Dhcp(serverAddress: "192.168.1.1", gateway: "192.168.1.1")).Dhcp!,
                Parse(Frames.Dhcp(serverAddress: "192.168.1.77", gateway: "192.168.1.77")).Dhcp!,
            ],
        };

        var mismatched = finding.Mismatched(["192.168.1.1"]);

        Assert.Single(mismatched);
        Assert.Equal("192.168.1.77", mismatched[0].ServerAddress);
    }

    [Fact(DisplayName = "Без знания своих шлюзов продукт никого не обвиняет")]
    public void WithoutGatewaysNothingIsClaimed()
    {
        var finding = new DhcpFinding { Sightings = [Parse(Frames.Dhcp()).Dhcp!] };

        Assert.Empty(finding.Mismatched([]));
    }

    // ---------------------------------------------------------------- оговорка о тишине

    [Fact(DisplayName = "Тишина за короткое окно — «не услышали», а не «нет»")]
    public void SilenceIsExplained()
    {
        var result = new CaptureResult
        {
            Adapter = new CaptureAdapter { Id = "x", Description = "Ethernet" },
            StartedUtc = When,
            Duration = TimeSpan.FromSeconds(30),
        };

        Assert.True(result.IsEmpty);
        Assert.Contains("раз в 30 секунд", result.Caveat, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Короткое окно оговаривается даже при удачном улове")]
    public void ShortWindowIsCaveated()
    {
        var result = new CaptureResult
        {
            Adapter = new CaptureAdapter { Id = "x", Description = "Ethernet" },
            StartedUtc = When,
            Duration = TimeSpan.FromSeconds(20),
            Neighbors = [Parse(Frames.Lldp()).Neighbor!],
        };

        Assert.False(result.IsEmpty);
        Assert.Contains("могло не попасть в это окно", result.Caveat, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Долгое прослушивание с уловом оговорок не требует")]
    public void LongWindowNeedsNoCaveat()
    {
        var result = new CaptureResult
        {
            Adapter = new CaptureAdapter { Id = "x", Description = "Ethernet" },
            StartedUtc = When,
            Duration = TimeSpan.FromMinutes(5),
            Neighbors = [Parse(Frames.Lldp()).Neighbor!],
        };

        Assert.Null(result.Caveat);
    }
}
