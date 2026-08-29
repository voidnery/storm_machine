using System.Globalization;
using System.Net;
using System.Text;
using PacketDotNet;
using PacketDotNet.DhcpV4;
using PacketDotNet.Lldp;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;

namespace StormMachine.Capture.Npcap;

/// <summary>Что удалось вычитать из одного кадра.</summary>
public sealed record FrameFinding(LinkNeighbor? Neighbor, DhcpSighting? Dhcp)
{
    public bool IsEmpty => Neighbor is null && Dhcp is null;
}

/// <summary>
/// Разбор кадров.
/// </summary>
/// <remarks>
/// Отделён от драйвера намеренно и целиком: разбор — это соглашения о байтах,
/// и проверять их надо на байтах, а не на живой сети. Драйвер захвата есть далеко
/// не у всех, а ошибиться в смещении TLV можно на любой машине.
/// <para>
/// Отсюда устройство: <see cref="Parse"/> принимает готовый массив байтов и ничего
/// не знает ни про адаптеры, ни про SharpPcap. Всё, что здесь есть, покрыто
/// проверками на записанных кадрах.
/// </para>
/// </remarks>
public static class FrameParser
{
    /// <summary>Адрес, на который шлются кадры CDP.</summary>
    private const string CdpDestination = "01000CCCCCCC";

    /// <summary>Идентификатор протокола CDP внутри заголовка SNAP.</summary>
    private const ushort CdpProtocol = 0x2000;

    /// <summary>Порт сервера DHCP: ответы идут с него.</summary>
    private const int DhcpServerPort = 67;

    /// <summary>
    /// Разбирает кадр Ethernet.
    /// </summary>
    /// <param name="localPort">Как называется адаптер, которым мы слушаем.</param>
    /// <param name="localIfIndex">Его <c>ifIndex</c>, если известен.</param>
    /// <remarks>
    /// Возвращает <c>null</c>, если кадр не из интересных. Это обычное дело:
    /// фильтр драйвера пропускает лишнее, а разбирать всё подряд продукту незачем.
    /// </remarks>
    public static FrameFinding? Parse(
        byte[] raw,
        DateTimeOffset observedUtc,
        string localPort,
        int localIfIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.Length < 14)
        {
            return null;
        }

        Packet packet;

        try
        {
            packet = Packet.ParsePacket(LinkLayers.Ethernet, raw);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            // Обрезанный или испорченный кадр. В эфире такое бывает, и падать из-за
            // него нельзя: одна битая посылка не должна обрывать прослушивание.
            return null;
        }

        if (packet.Extract<LldpPacket>() is { } lldp)
        {
            return new FrameFinding(FromLldp(lldp, observedUtc, localPort, localIfIndex), null);
        }

        if (FromCdp(raw, observedUtc, localPort, localIfIndex) is { } cdp)
        {
            return new FrameFinding(cdp, null);
        }

        if (packet.Extract<UdpPacket>() is { SourcePort: DhcpServerPort } udp)
        {
            // MAC отправителя берётся из самого кадра, а не через цепочку родительских
            // пакетов: цепочка восстанавливается библиотекой не всегда, а смещение
            // в кадре Ethernet неизменно — шесть байт адреса назначения, затем шесть
            // адреса источника.
            return new FrameFinding(null, FromDhcp(udp, observedUtc, Mac(raw, 6)));
        }

        return null;
    }

    // -------------------------------------------------------------------------- LLDP

    /// <summary>
    /// Собирает соседа из TLV.
    /// </summary>
    /// <remarks>
    /// Порт нашей стороны в кадре не написан — и не может быть: кадр пришёл к нам,
    /// а не рассказывает про нас. Поэтому наш конец связи берётся от адаптера,
    /// которым слушали. В этом и разница с опросом по SNMP: тот отвечает за все порты
    /// коммутатора сразу, захват — только за тот, куда воткнуты мы.
    /// </remarks>
    private static LinkNeighbor FromLldp(
        LldpPacket lldp,
        DateTimeOffset observedUtc,
        string localPort,
        int localIfIndex)
    {
        string? name = null;
        string? description = null;
        string? portId = null;
        string? portDescription = null;
        string? chassis = null;
        string? management = null;

        foreach (var item in lldp)
        {
            switch (item)
            {
                // Строковые TLV читаются из БАЙТОВ, а не через свойство Value:
                // библиотека декодирует их не в UTF-8, и «серверная, стойка 2»
                // превращается в вопросительные знаки. Подписи портов на объектах
                // пишут по-русски, так что это не мелочь.
                case SystemNameTlv value:
                    name = Value(value);
                    break;

                case SystemDescriptionTlv value:
                    description = Value(value);
                    break;

                case PortDescriptionTlv value:
                    portDescription = Value(value);
                    break;

                case PortIdTlv value:
                    portId = Describe(value.SubTypeValue);
                    break;

                case ChassisIdTlv value:
                    chassis = Describe(value.SubTypeValue);
                    break;

                case ManagementAddressTlv value:
                    management = Blank(value.Address?.ToString());
                    break;

                default:
                    break;
            }
        }

        return new LinkNeighbor
        {
            Protocol = NeighborProtocol.Lldp,
            Source = NeighborSource.Capture,
            LocalIfIndex = localIfIndex,
            LocalPort = localPort,
            RemoteName = name,
            RemoteDescription = description,
            RemotePort = portId,
            RemotePortDescription = portDescription,
            RemoteChassisId = chassis,
            RemoteAddress = management,
            ObservedUtc = observedUtc,
        };
    }

    /// <summary>Значение строкового TLV, прочитанное из его собственных байтов.</summary>
    /// <remarks>
    /// Первые два байта TLV — тип и длина; дальше идёт значение. Библиотека даёт
    /// готовую строку, но декодирует её не в UTF-8 — отсюда чтение байтов напрямую.
    /// </remarks>
    private static string? Value(Tlv tlv)
    {
        var bytes = tlv.Bytes;

        return bytes.Length <= 2 ? null : Text(bytes.AsSpan(2));
    }

    // --------------------------------------------------------------------------- CDP

    /// <summary>
    /// Разбирает кадр CDP.
    /// </summary>
    /// <remarks>
    /// Руками, потому что PacketDotNet его не знает. Обходится это дёшево: заголовок
    /// SNAP фиксированной длины и простые TLV «тип, длина, значение». А сети, где LLDP
    /// выключен, а CDP включён, встречаются достаточно часто, чтобы ради них
    /// написать шестьдесят строк.
    /// </remarks>
    private static LinkNeighbor? FromCdp(
        byte[] raw,
        DateTimeOffset observedUtc,
        string localPort,
        int localIfIndex)
    {
        // Кадр CDP узнаётся по адресу назначения и по идентификатору протокола
        // внутри SNAP. Заголовок: 6 байт адреса назначения, 6 источника, 2 длины,
        // затем LLC (AA AA 03), OUI (00 00 0C) и два байта протокола — итого 22.
        if (raw.Length < 26 || Hex(raw, 0, 6) != CdpDestination)
        {
            return null;
        }

        if (raw[14] != 0xAA || raw[15] != 0xAA || raw[16] != 0x03)
        {
            return null;
        }

        if (Read16(raw, 20) != CdpProtocol)
        {
            return null;
        }

        // Заголовок CDP: версия, время жизни, контрольная сумма — четыре байта.
        var at = 26;

        string? device = null;
        string? port = null;
        string? platform = null;
        string? version = null;

        while (at + 4 <= raw.Length)
        {
            var type = Read16(raw, at);
            var length = Read16(raw, at + 2);

            // Длина считается вместе с заголовком TLV. Меньше четырёх — кадр битый,
            // и продолжать разбор нельзя: сместимся не туда и прочитаем мусор.
            if (length < 4 || at + length > raw.Length)
            {
                break;
            }

            var value = raw.AsSpan(at + 4, length - 4);

            switch (type)
            {
                case 0x0001:
                    device = Text(value);
                    break;

                case 0x0003:
                    port = Text(value);
                    break;

                case 0x0005:
                    version = Text(value);
                    break;

                case 0x0006:
                    platform = Text(value);
                    break;

                default:
                    break;
            }

            at += length;
        }

        if (device is null && port is null)
        {
            return null;
        }

        return new LinkNeighbor
        {
            Protocol = NeighborProtocol.Cdp,
            Source = NeighborSource.Capture,
            LocalIfIndex = localIfIndex,
            LocalPort = localPort,
            RemoteName = device,
            RemotePort = port,
            RemoteDescription = platform ?? version,
            RemoteChassisId = Mac(raw, 6),
            ObservedUtc = observedUtc,
        };
    }

    // -------------------------------------------------------------------------- DHCP

    /// <summary>
    /// Разбирает ответ DHCP.
    /// </summary>
    /// <remarks>
    /// Адрес сервера берётся из опции 54, а не из адреса отправителя: отправителем
    /// может быть агент ретрансляции, который чужие ответы пересылает, а сам сервером
    /// не является. Спутать их значит обвинить не того.
    /// </remarks>
    private static DhcpSighting? FromDhcp(UdpPacket udp, DateTimeOffset observedUtc, string senderMac)
    {
        if (udp.Extract<DhcpV4Packet>() is not { } dhcp)
        {
            return null;
        }

        string? server = null;
        string? gateway = null;
        var dns = new List<string>();
        var message = DhcpMessage.Other;

        foreach (var option in dhcp.GetOptions())
        {
            switch (option.OptionType)
            {
                case DhcpV4OptionType.DHCPServerId:
                    server = Address(option.Data, 0);
                    break;

                case DhcpV4OptionType.Router:
                    gateway = Address(option.Data, 0);
                    break;

                case DhcpV4OptionType.DomainServer:
                    for (var i = 0; i + 4 <= option.Data.Length; i += 4)
                    {
                        if (Address(option.Data, i) is { } address)
                        {
                            dns.Add(address);
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        message = dhcp.MessageType switch
        {
            DhcpV4MessageType.Offer => DhcpMessage.Offer,
            DhcpV4MessageType.Ack => DhcpMessage.Ack,
            DhcpV4MessageType.Nak => DhcpMessage.Nak,
            _ => DhcpMessage.Other,
        };

        // Ответ без опции 54 бывает у совсем простых серверов. Тогда за сервер
        // считается отправитель — это менее надёжно, и лучше так, чем потерять факт.
        server ??= (udp.ParentPacket as IPv4Packet)?.SourceAddress?.ToString();

        if (server is null)
        {
            return null;
        }

        return new DhcpSighting
        {
            ServerAddress = server,
            ServerMac = senderMac,
            Message = message,
            OfferedAddress = Blank(dhcp.YourAddress?.ToString()) is { } offered && offered != "0.0.0.0"
                ? offered
                : null,
            OfferedGateway = gateway,
            OfferedDns = dns,
            ObservedUtc = observedUtc,
        };
    }

    // ------------------------------------------------------------------ вспомогательное

    private static string? Describe(object? value) => value switch
    {
        null => null,
        byte[] bytes => Text(bytes),
        System.Net.NetworkInformation.PhysicalAddress mac => Format(mac),
        IPAddress address => address.ToString(),
        NetworkAddress address => Blank(address.ToString()),
        _ => Blank(value.ToString()),
    };

    private static string Format(System.Net.NetworkInformation.PhysicalAddress mac) =>
        string.Join('-', mac.GetAddressBytes().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Байты как строка.
    /// </summary>
    /// <remarks>
    /// Кириллица в подписях портов встречается на объектах постоянно, поэтому UTF-8.
    /// Непечатаемое показывается шестнадцатеричным: идентификатор шасси бывает шестью
    /// байтами MAC-адреса, и выводить их как текст значит сломать вывод.
    /// </remarks>
    private static string? Text(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        var printable = true;

        foreach (var b in value)
        {
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D))
            {
                printable = false;

                break;
            }
        }

        if (!printable)
        {
            return string.Join('-', value.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        }

        return Blank(Encoding.UTF8.GetString(value).Replace('\r', ' ').Replace('\n', ' '));
    }

    private static string? Address(byte[] data, int offset) => data.Length >= offset + 4
        ? new IPAddress(data.AsSpan(offset, 4).ToArray()).ToString()
        : null;

    private static string Mac(byte[] raw, int offset) =>
        string.Join('-', raw.Skip(offset).Take(6).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private static string Hex(byte[] raw, int offset, int length) =>
        string.Concat(raw.Skip(offset).Take(length).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private static ushort Read16(byte[] raw, int offset) => (ushort)((raw[offset] << 8) | raw[offset + 1]);

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
