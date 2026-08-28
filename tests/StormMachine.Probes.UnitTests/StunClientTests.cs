using System.Buffers.Binary;
using System.Net;
using StormMachine.Probes;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// Проверки разбора ответа STUN (RFC 5389).
/// </summary>
/// <remarks>
/// Разбор двоичного формата с XOR-маской и выравниванием атрибутов — код, ошибку
/// в котором глазами не видно: неверный адрес выглядит как адрес. Ответы собираются
/// здесь вручную по букве стандарта, чтобы проверять разбор, а не сеть.
/// </remarks>
public sealed class StunClientTests
{
    private const uint MagicCookie = 0x2112_A442;

    private static readonly byte[] TransactionId =
        [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC];

    /// <summary>Собирает ответ Binding Success с одним атрибутом.</summary>
    private static byte[] Response(ushort attributeType, byte[] value, byte[]? transactionId = null)
    {
        var id = transactionId ?? TransactionId;
        var padded = (value.Length + 3) & ~3;
        var packet = new byte[20 + 4 + padded];

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(4 + padded));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        id.CopyTo(packet.AsSpan(8));

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), attributeType);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)value.Length);
        value.CopyTo(packet.AsSpan(24));

        return packet;
    }

    private static byte[] XorMappedV4(string address, int port)
    {
        var value = new byte[8];
        value[0] = 0;
        value[1] = 0x01;

        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), (ushort)(port ^ (MagicCookie >> 16)));

        var bytes = IPAddress.Parse(address).GetAddressBytes();

        for (var i = 0; i < 4; i++)
        {
            value[4 + i] = (byte)(bytes[i] ^ (byte)(MagicCookie >> ((3 - i) * 8)));
        }

        return value;
    }

    [Fact]
    public void ReadsXorMappedAddress()
    {
        var packet = Response(0x0020, XorMappedV4("203.0.113.7", 54321));

        var endpoint = StunClient.ParseResponse(packet, TransactionId);

        Assert.NotNull(endpoint);
        Assert.Equal("203.0.113.7", endpoint.Address.ToString());
        Assert.Equal(54321, endpoint.Port);
    }

    [Fact]
    public void ReadsLegacyMappedAddressWhenNoXorAttribute()
    {
        // Старый MAPPED-ADDRESS (RFC 3489) без маски. Некоторые серверы шлют только его.
        var value = new byte[8];
        value[1] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), 3478);
        IPAddress.Parse("198.51.100.9").GetAddressBytes().CopyTo(value, 4);

        var endpoint = StunClient.ParseResponse(Response(0x0001, value), TransactionId);

        Assert.NotNull(endpoint);
        Assert.Equal("198.51.100.9", endpoint.Address.ToString());
        Assert.Equal(3478, endpoint.Port);
    }

    [Fact]
    public void RejectsForeignTransaction()
    {
        // На тот же порт может прийти чужой пакет. Принять его значило бы сообщить
        // оператору чужой внешний адрес — и вывод о NAT был бы сделан не про него.
        var packet = Response(0x0020, XorMappedV4("203.0.113.7", 54321), [.. Enumerable.Repeat((byte)0xEE, 12)]);

        Assert.Null(StunClient.ParseResponse(packet, TransactionId));
    }

    [Fact]
    public void RejectsWrongMagicCookie()
    {
        var packet = Response(0x0020, XorMappedV4("203.0.113.7", 54321));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0xDEAD_BEEF);

        Assert.Null(StunClient.ParseResponse(packet, TransactionId));
    }

    [Fact]
    public void RejectsTruncatedPacket() =>
        Assert.Null(StunClient.ParseResponse([0x01, 0x01], TransactionId));

    [Fact]
    public void SkipsUnknownAttributesWithPadding()
    {
        // Атрибут нечётной длины дополняется до кратности четырём (RFC 5389 §15).
        // Без учёта дополнения разбор уехал бы и не нашёл следующий атрибут.
        var software = new byte[] { 0x74, 0x65, 0x73, 0x74, 0x21 };
        var mapped = XorMappedV4("203.0.113.7", 54321);

        var paddedSoftware = (software.Length + 3) & ~3;
        var packet = new byte[20 + 4 + paddedSoftware + 4 + mapped.Length];

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length - 20));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        TransactionId.CopyTo(packet.AsSpan(8));

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 0x8022);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)software.Length);
        software.CopyTo(packet.AsSpan(24));

        var at = 24 + paddedSoftware;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at + 2), (ushort)mapped.Length);
        mapped.CopyTo(packet.AsSpan(at + 4));

        var endpoint = StunClient.ParseResponse(packet, TransactionId);

        Assert.NotNull(endpoint);
        Assert.Equal("203.0.113.7", endpoint.Address.ToString());
    }

    [Fact]
    public void RequestCarriesMagicCookieAndTransaction()
    {
        var request = StunClient.BuildRequest([.. TransactionId]);

        Assert.Equal(20, request.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(request));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)));
        Assert.Equal(MagicCookie, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4)));
        Assert.True(request.AsSpan(8, 12).SequenceEqual(TransactionId));
    }
}
