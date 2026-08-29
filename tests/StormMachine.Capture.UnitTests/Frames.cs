namespace StormMachine.Capture.UnitTests;

/// <summary>
/// Сборка кадров байт за байтом.
/// </summary>
/// <remarks>
/// Намеренно вручную, а не средствами той же библиотеки, которой идёт разбор.
/// Кадр, собранный чужим кодом и разобранный им же, проверяет согласованность
/// библиотеки с самой собой; ошибку в <b>нашем</b> понимании формата — смещение TLV,
/// перепутанный порядок байтов, забытый заголовок SNAP — он не поймает.
/// <para>
/// Раскладки взяты из стандартов: IEEE 802.1AB для LLDP, RFC 2131 и 2132 для DHCP,
/// заголовок SNAP из IEEE 802.2 для CDP.
/// </para>
/// </remarks>
internal static class Frames
{
    public static readonly byte[] OurMac = [0x00, 0x50, 0x56, 0x11, 0x22, 0x33];

    public static readonly byte[] TheirMac = [0x00, 0x1C, 0x0E, 0xAA, 0xBB, 0x01];

    /// <summary>Групповой адрес LLDP — IEEE 802.1AB §8.1.</summary>
    private static readonly byte[] LldpGroup = [0x01, 0x80, 0xC2, 0x00, 0x00, 0x0E];

    /// <summary>Групповой адрес CDP.</summary>
    private static readonly byte[] CdpGroup = [0x01, 0x00, 0x0C, 0xCC, 0xCC, 0xCC];

    /// <summary>
    /// Кадр LLDP.
    /// </summary>
    /// <remarks>
    /// TLV устроен так: семь бит типа, девять бит длины, затем значение. Отсюда
    /// сдвиг на один бит в первом байте — самое частое место ошибки при разборе,
    /// и именно его надо проверять на настоящих байтах.
    /// </remarks>
    public static byte[] Lldp(
        string systemName = "sw-core-01",
        string portId = "Te1/0/24",
        string? portDescription = "к доступу, стойка 2",
        string? systemDescription = "Core switch, firmware 4.2")
    {
        var body = new List<byte>();

        // Тип 1 — идентификатор шасси, подтип 4 — MAC-адрес.
        body.AddRange(Tlv(1, [4, .. TheirMac]));

        // Тип 2 — идентификатор порта, подтип 5 — имя интерфейса.
        body.AddRange(Tlv(2, [5, .. Text(portId)]));

        // Тип 3 — время жизни, две секунды по стандарту записываются словом.
        body.AddRange(Tlv(3, [0x00, 0x78]));

        if (portDescription is not null)
        {
            body.AddRange(Tlv(4, Text(portDescription)));
        }

        body.AddRange(Tlv(5, Text(systemName)));

        if (systemDescription is not null)
        {
            body.AddRange(Tlv(6, Text(systemDescription)));
        }

        // Тип 0 нулевой длины — конец блока.
        body.AddRange(Tlv(0, []));

        return [.. LldpGroup, .. TheirMac, 0x88, 0xCC, .. body];
    }

    /// <summary>
    /// Кадр CDP.
    /// </summary>
    /// <remarks>
    /// В отличие от LLDP идёт не поверх EtherType, а внутри LLC/SNAP: длина кадра,
    /// три байта LLC, три байта OUI и два байта протокола. Пропустить SNAP — значит
    /// прочитать TLV со сдвигом в восемь байт и получить мусор, который выглядит
    /// как данные.
    /// </remarks>
    public static byte[] Cdp(
        string deviceId = "sw-access-02",
        string portId = "GigabitEthernet1/0/1",
        string platform = "cisco WS-C2960X")
    {
        var body = new List<byte>
        {
            // Версия и время жизни, затем контрольная сумма — её мы не считаем:
            // разбор её не проверяет, а сеть проверяет своими средствами.
            0x02, 0xB4, 0x00, 0x00,
        };

        body.AddRange(CdpTlv(0x0001, Text(deviceId)));
        body.AddRange(CdpTlv(0x0003, Text(portId)));
        body.AddRange(CdpTlv(0x0006, Text(platform)));

        var length = body.Count + 8;

        return
        [
            .. CdpGroup,
            .. TheirMac,
            (byte)(length >> 8), (byte)(length & 0xFF),
            0xAA, 0xAA, 0x03,
            0x00, 0x00, 0x0C,
            0x20, 0x00,
            .. body,
        ];
    }

    /// <summary>
    /// Ответ DHCP: широковещательный OFFER от сервера.
    /// </summary>
    /// <remarks>
    /// Собирается целиком — Ethernet, IPv4, UDP и тело BOOTP с опциями. Длины
    /// в заголовках проставляются настоящие: разбор их читает, и кадр с неверной
    /// длиной он отвергнет — как отверг бы и настоящий.
    /// </remarks>
    public static byte[] Dhcp(
        string serverAddress = "192.168.1.1",
        string offeredAddress = "192.168.1.50",
        string? gateway = "192.168.1.1",
        string? dns = "192.168.1.1",
        byte messageType = 2)
    {
        var options = new List<byte>
        {
            53, 1, messageType,
        };

        options.AddRange([54, 4, .. Ip(serverAddress)]);

        if (gateway is not null)
        {
            options.AddRange([3, 4, .. Ip(gateway)]);
        }

        if (dns is not null)
        {
            options.AddRange([6, 4, .. Ip(dns)]);
        }

        options.Add(255);

        var dhcp = new List<byte>
        {
            2, 1, 6, 0,                       // ответ, Ethernet, длина адреса, хопы
            0x39, 0x03, 0xF3, 0x26,           // идентификатор транзакции
            0x00, 0x00,                       // секунды
            0x80, 0x00,                       // флаг широковещательного ответа
        };

        dhcp.AddRange(new byte[4]);           // адрес клиента
        dhcp.AddRange(Ip(offeredAddress));    // предложенный адрес
        dhcp.AddRange(Ip(serverAddress));     // адрес следующего сервера
        dhcp.AddRange(new byte[4]);           // агент ретрансляции
        dhcp.AddRange([.. OurMac, .. new byte[10]]);
        dhcp.AddRange(new byte[64 + 128]);    // имя сервера и имя файла
        dhcp.AddRange([0x63, 0x82, 0x53, 0x63]);
        dhcp.AddRange(options);

        var udpLength = dhcp.Count + 8;
        var udp = new List<byte>
        {
            0x00, 0x43,                       // порт отправителя: 67
            0x00, 0x44,                       // порт получателя: 68
            (byte)(udpLength >> 8), (byte)(udpLength & 0xFF),
            0x00, 0x00,                       // контрольная сумма не считается
        };

        udp.AddRange(dhcp);

        var ipLength = udp.Count + 20;
        var ip = new List<byte>
        {
            0x45, 0x00,
            (byte)(ipLength >> 8), (byte)(ipLength & 0xFF),
            0x00, 0x00, 0x00, 0x00,
            0x40, 0x11,                       // время жизни и протокол UDP
            0x00, 0x00,                       // контрольная сумма не считается
        };

        ip.AddRange(Ip(serverAddress));
        ip.AddRange([255, 255, 255, 255]);
        ip.AddRange(udp);

        return [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. TheirMac, 0x08, 0x00, .. ip];
    }

    private static byte[] Tlv(int type, byte[] value)
    {
        var header = (type << 9) | value.Length;

        return [(byte)(header >> 8), (byte)(header & 0xFF), .. value];
    }

    private static byte[] CdpTlv(int type, byte[] value)
    {
        var length = value.Length + 4;

        return
        [
            (byte)(type >> 8), (byte)(type & 0xFF),
            (byte)(length >> 8), (byte)(length & 0xFF),
            .. value,
        ];
    }

    private static byte[] Text(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    private static byte[] Ip(string address) => System.Net.IPAddress.Parse(address).GetAddressBytes();
}
