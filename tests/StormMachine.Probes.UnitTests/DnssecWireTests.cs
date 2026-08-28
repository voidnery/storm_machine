using System.Buffers.Binary;
using StormMachine.Probes;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// Проверки запроса и разбора DNSSEC.
/// </summary>
/// <remarks>
/// Здесь закрепляются два утверждения, которые продукт делает, и одно, которого он
/// не делает. Делает: зона подписана (по наличию RRSIG в ответе — это факт из проволоки)
/// и резолвер заявил, что подпись проверил (флаг AD). Не делает: «подпись верна» —
/// для такого утверждения нужна цепочка доверия от корневого ключа, а её ведение
/// есть работа резолвера, а не измерителя.
/// </remarks>
public sealed class DnssecWireTests
{
    private const int HeaderLength = 12;

    [Fact]
    public void QueryWithoutDnssec_CarriesNoAdditionalRecords()
    {
        var query = DnsWire.BuildQuery(0x1234, "example.com", DnsWire.RecordTypeA);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(10)));
    }

    [Fact]
    public void QueryWithDnssec_AppendsOptRecordWithDoBit()
    {
        var query = DnsWire.BuildQuery(0x1234, "example.com", DnsWire.RecordTypeA, dnssecOk: true);

        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(10)));

        // Псевдозапись OPT в конце: корневое имя, тип 41, «класс» — размер ответа,
        // старший бит TTL — DO (RFC 6891 §6.1.2).
        var at = query.Length - 11;

        Assert.Equal(0, query[at]);
        Assert.Equal(DnsWire.RecordTypeOpt, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(at + 1)));

        var ttl = BinaryPrimitives.ReadUInt32BigEndian(query.AsSpan(at + 5));

        Assert.Equal(0x8000u, ttl & 0x8000u);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(at + 9)));
    }

    [Fact]
    public void Parse_ReadsAuthenticDataFlag()
    {
        // AD — утверждение резолвера, и продукт называет его именно так.
        Assert.True(DnsWire.Parse(Answer(flags: 0x8180 | 0x0020)).IsAuthenticData);
        Assert.False(DnsWire.Parse(Answer(flags: 0x8180)).IsAuthenticData);
    }

    [Fact]
    public void Parse_ReadsRrsigAndCallsZoneSigned()
    {
        var response = DnsWire.Parse(AnswerWithRrsig());

        Assert.True(response.IsZoneSigned);

        var signature = Assert.Single(response.Answers, a => a.Type == "RRSIG");

        Assert.Contains("A", signature.Value, StringComparison.Ordinal);
        Assert.Contains("example.com", signature.Value, StringComparison.Ordinal);
        Assert.Contains("годна до", signature.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnsignedZoneIsNotSigned() => Assert.False(DnsWire.Parse(Answer(0x8180)).IsZoneSigned);

    /// <summary>Ответ с одной записью A.</summary>
    private static byte[] Answer(int flags)
    {
        var name = EncodeName("example.com");
        var packet = new byte[HeaderLength + name.Length + 4 + name.Length + 10 + 4];

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)flags);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 1);

        var at = HeaderLength;
        name.CopyTo(packet.AsSpan(at));
        at += name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at), DnsWire.RecordTypeA);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at + 2), 1);
        at += 4;

        name.CopyTo(packet.AsSpan(at));
        at += name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at), DnsWire.RecordTypeA);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at + 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(at + 4), 300);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at + 8), 4);
        at += 10;

        packet[at] = 93;
        packet[at + 1] = 184;
        packet[at + 2] = 216;
        packet[at + 3] = 34;

        return packet;
    }

    /// <summary>Ответ с записью A и подписью RRSIG к ней.</summary>
    private static byte[] AnswerWithRrsig()
    {
        var signer = EncodeName("example.com");

        // RRSIG: тип покрытия (2), алгоритм (1), меток (1), исходный TTL (4),
        // истечение (4), начало (4), метка ключа (2), имя подписавшего, подпись.
        var rdata = new byte[18 + signer.Length + 4];

        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(0), DnsWire.RecordTypeA);
        rdata[2] = 13;
        rdata[3] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(rdata.AsSpan(4), 300);
        BinaryPrimitives.WriteUInt32BigEndian(rdata.AsSpan(8), 1_800_000_000);
        BinaryPrimitives.WriteUInt32BigEndian(rdata.AsSpan(12), 1_700_000_000);
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(16), 12345);
        signer.CopyTo(rdata.AsSpan(18));

        var name = EncodeName("example.com");
        var packet = new byte[HeaderLength + name.Length + 4 + name.Length + 10 + rdata.Length];

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 1);

        var at = HeaderLength;
        name.CopyTo(packet.AsSpan(at));
        at += name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at), DnsWire.RecordTypeA);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at + 2), 1);
        at += 4;

        name.CopyTo(packet.AsSpan(at));
        at += name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at), DnsWire.RecordTypeRrsig);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at + 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(at + 4), 300);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at + 8), (ushort)rdata.Length);
        at += 10;

        rdata.CopyTo(packet.AsSpan(at));

        return packet;
    }

    private static byte[] EncodeName(string name)
    {
        var labels = name.Split('.');
        var bytes = new byte[labels.Sum(l => 1 + l.Length) + 1];
        var at = 0;

        foreach (var label in labels)
        {
            bytes[at++] = (byte)label.Length;

            foreach (var c in label)
            {
                bytes[at++] = (byte)c;
            }
        }

        return bytes;
    }
}
