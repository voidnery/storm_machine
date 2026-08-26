using System.Buffers.Binary;
using System.Text;
using StormMachine.Probes;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// Проверки формирования и разбора пакетов DNS.
/// </summary>
/// <remarks>
/// Разбор написан вручную и содержит два места, где ошибка не проявилась бы сразу:
/// сжатие имён по указателям и защита от указателя на самого себя. Первое дало бы
/// испорченные имена в отчёте, второе — зависание инструмента на повреждённом ответе.
/// </remarks>
public sealed class DnsWireTests
{
    [Fact]
    public void BuildQuery_WritesHeaderAndQuestion()
    {
        var packet = DnsWire.BuildQuery(0xABCD, "example.com", DnsWire.RecordTypeA);

        Assert.Equal(0xABCD, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0)));

        // Флаг «рекурсия желательна».
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));

        // Ровно один вопрос и ни одного ответа.
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)));

        // Имя в виде меток: 7 example 3 com 0
        Assert.Equal(7, packet[12]);
        Assert.Equal("example", Encoding.ASCII.GetString(packet, 13, 7));
        Assert.Equal(3, packet[20]);
        Assert.Equal("com", Encoding.ASCII.GetString(packet, 21, 3));
        Assert.Equal(0, packet[24]);
    }

    [Fact]
    public void BuildQuery_IgnoresTrailingDot()
    {
        var withDot = DnsWire.BuildQuery(1, "example.com.", DnsWire.RecordTypeA);
        var withoutDot = DnsWire.BuildQuery(1, "example.com", DnsWire.RecordTypeA);

        Assert.Equal(withoutDot, withDot);
    }

    [Theory]
    [InlineData("A", DnsWire.RecordTypeA)]
    [InlineData("aaaa", DnsWire.RecordTypeAaaa)]
    [InlineData("MX", DnsWire.RecordTypeMx)]
    [InlineData("txt", DnsWire.RecordTypeTxt)]
    public void ParseRecordType_IsCaseInsensitive(string name, ushort expected)
    {
        Assert.Equal(expected, DnsWire.ParseRecordType(name));
    }

    [Fact]
    public void ParseRecordType_RejectsUnknown()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.ParseRecordType("НЕТ_ТАКОГО"));
    }

    [Fact]
    public void Parse_ReadsARecord()
    {
        var packet = BuildResponse(
            questionName: "example.com",
            answers: [(0xC00C, DnsWire.RecordTypeA, 300, [93, 184, 216, 34])]);

        var response = DnsWire.Parse(packet);

        Assert.True(response.IsSuccess);
        Assert.Equal("NOERROR", response.ResponseCodeName);
        var record = Assert.Single(response.Answers);
        Assert.Equal("A", record.Type);
        Assert.Equal("93.184.216.34", record.Value);
        Assert.Equal(300u, record.Ttl);
    }

    [Fact]
    public void Parse_ExpandsCompressedName()
    {
        // Имя ответа задано указателем на вопрос (0xC00C) — обычная практика серверов.
        var packet = BuildResponse(
            questionName: "storm.example.com",
            answers: [(0xC00C, DnsWire.RecordTypeA, 60, [10, 0, 0, 1])]);

        var response = DnsWire.Parse(packet);

        var record = Assert.Single(response.Answers);
        Assert.Equal("storm.example.com", record.Name);
    }

    [Fact]
    public void Parse_SurvivesSelfReferencingPointer()
    {
        // Повреждённый или злонамеренный ответ: указатель ссылается сам на себя.
        // Наивный разбор здесь зациклится, а инструмент диагностики обязан устоять.
        var packet = new byte[64];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 1);

        // Ответ начинается сразу после заголовка: имя — указатель на самого себя.
        packet[12] = 0xC0;
        packet[13] = 12;

        var exception = Record.Exception(() => DnsWire.Parse(packet));

        Assert.Null(exception);
    }

    [Fact]
    public void Parse_RejectsPacketShorterThanHeader()
    {
        Assert.Throws<FormatException>(() => DnsWire.Parse(new byte[4]));
    }

    [Fact]
    public void Parse_ReadsResponseCode()
    {
        var packet = BuildResponse("nope.invalid", [], responseCode: 3);

        var response = DnsWire.Parse(packet);

        Assert.False(response.IsSuccess);
        Assert.Equal("NXDOMAIN", response.ResponseCodeName);
        Assert.Empty(response.Answers);
    }

    [Fact]
    public void Parse_DetectsTruncation()
    {
        var packet = BuildResponse("big.example.com", [], truncated: true);

        var response = DnsWire.Parse(packet);

        Assert.True(response.IsTruncated);
    }

    [Fact]
    public void Parse_StopsOnDeclaredLengthBeyondPacket()
    {
        // Заявленная длина данных больше самого пакета — разбор не должен выйти за границы.
        var packet = BuildResponse(
            "example.com",
            [(0xC00C, DnsWire.RecordTypeA, 60, [1, 2, 3, 4])]);

        // Портим длину данных последней записи.
        var lengthOffset = packet.Length - 4 - 2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(lengthOffset), 4000);

        var response = DnsWire.Parse(packet);

        Assert.Empty(response.Answers);
    }

    /// <summary>Собирает ответ DNS для тестов.</summary>
    private static byte[] BuildResponse(
        string questionName,
        (ushort NamePointer, ushort Type, uint Ttl, byte[] Data)[] answers,
        int responseCode = 0,
        bool truncated = false)
    {
        var question = DnsWire.BuildQuery(1, questionName, DnsWire.RecordTypeA);
        var packet = new List<byte>(question);

        var flags = (ushort)(0x8180 | responseCode);
        if (truncated)
        {
            flags |= 0x0200;
        }

        var header = packet.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)answers.Length);

        packet.Clear();
        packet.AddRange(header);

        foreach (var (namePointer, type, ttl, data) in answers)
        {
            var record = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(0), type);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), ttl);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(8), (ushort)data.Length);

            packet.Add((byte)(namePointer >> 8));
            packet.Add((byte)(namePointer & 0xFF));
            packet.AddRange(record);
            packet.AddRange(data);
        }

        return [.. packet];
    }
}
