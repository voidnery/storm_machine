using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace StormMachine.Probes;

/// <summary>Разобранная запись ответа DNS.</summary>
public sealed record DnsRecord(string Name, string Type, uint Ttl, string Value);

/// <summary>Разобранный ответ DNS.</summary>
public sealed record DnsResponse
{
    public required int ResponseCode { get; init; }

    public required bool IsAuthoritative { get; init; }

    public required bool IsTruncated { get; init; }

    /// <summary>
    /// Резолвер сообщил, что проверил подписи (флаг AD, RFC 4035 §3.2.3).
    /// </summary>
    /// <remarks>
    /// Это утверждение резолвера, а не наша проверка. Собственная проверка потребовала бы
    /// цепочки доверия от корневого ключа, и выдавать чужое «я проверил» за своё значило бы
    /// сообщать оператору уверенность, которой у нас нет. Флаг честен ровно в одном: он
    /// отличает резолвер, который проверяет, от резолвера, который не проверяет.
    /// </remarks>
    public required bool IsAuthenticData { get; init; }

    public required IReadOnlyList<DnsRecord> Answers { get; init; }

    /// <summary>Зона подписана: в ответе пришли RRSIG. Не зависит от того, кто спрашивал.</summary>
    public bool IsZoneSigned => Answers.Any(a => a.Type == "RRSIG");

    /// <summary>Текстовое имя кода ответа — то, что понятно оператору.</summary>
    public string ResponseCodeName => ResponseCode switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => $"RCODE {ResponseCode}",
    };

    public bool IsSuccess => ResponseCode == 0;
}

/// <summary>
/// Формирование и разбор пакетов DNS (RFC 1035).
/// </summary>
/// <remarks>
/// Написано вручную, а не взято готовой библиотекой, по одной причине: измерительному
/// инструменту нужен контроль над тем, что именно и когда уходит в сеть. Готовый резолвер
/// кэширует, повторяет запросы и сам выбирает сервер — и любое из этих действий превращает
/// измерение задержки в измерение поведения библиотеки.
/// </remarks>
internal static class DnsWire
{
    public const ushort RecordTypeA = 1;
    public const ushort RecordTypeNs = 2;
    public const ushort RecordTypeCname = 5;
    public const ushort RecordTypeSoa = 6;
    public const ushort RecordTypePtr = 12;
    public const ushort RecordTypeMx = 15;
    public const ushort RecordTypeTxt = 16;
    public const ushort RecordTypeAaaa = 28;
    public const ushort RecordTypeRrsig = 46;
    public const ushort RecordTypeOpt = 41;

    private const int HeaderLength = 12;
    private const int MaxPointerJumps = 64;

    public static ushort ParseRecordType(string name) => name.ToUpperInvariant() switch
    {
        "A" => RecordTypeA,
        "AAAA" => RecordTypeAaaa,
        "NS" => RecordTypeNs,
        "CNAME" => RecordTypeCname,
        "SOA" => RecordTypeSoa,
        "PTR" => RecordTypePtr,
        "MX" => RecordTypeMx,
        "TXT" => RecordTypeTxt,
        _ => throw new ArgumentException($"Неизвестный тип записи: {name}", nameof(name)),
    };

    public static string RecordTypeName(ushort type) => type switch
    {
        RecordTypeA => "A",
        RecordTypeAaaa => "AAAA",
        RecordTypeNs => "NS",
        RecordTypeCname => "CNAME",
        RecordTypeSoa => "SOA",
        RecordTypePtr => "PTR",
        RecordTypeMx => "MX",
        RecordTypeTxt => "TXT",
        RecordTypeRrsig => "RRSIG",
        RecordTypeOpt => "OPT",
        _ => $"TYPE{type}",
    };

    /// <summary>Размер ответа, который мы готовы принять по UDP (EDNS0, RFC 6891).</summary>
    private const ushort EdnsPayloadSize = 1232;

    private const int OptRecordLength = 11;

    /// <param name="dnssecOk">
    /// Запросить подписи: EDNS0 с установленным битом DO. По умолчанию выключено —
    /// обычное приложение подписей не просит, а измерять надо то, что получит оно.
    /// Включение меняет и размер ответа, и время: сравнивать с выключенным нельзя.
    /// </param>
    public static byte[] BuildQuery(ushort id, string name, ushort recordType, bool dnssecOk = false)
    {
        ArgumentNullException.ThrowIfNull(name);

        var labels = name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var questionLength = labels.Sum(l => 1 + Encoding.ASCII.GetByteCount(l)) + 1 + 4;

        var packet = new byte[HeaderLength + questionLength + (dnssecOk ? OptRecordLength : 0)];

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), id);

        // Флаги: рекурсия желательна. Кэш не отключаем — измеряем то, что получит
        // обычное приложение, а не искусственно худший случай.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);

        if (dnssecOk)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), 1);
        }

        var offset = HeaderLength;
        foreach (var label in labels)
        {
            var written = Encoding.ASCII.GetBytes(label, 0, label.Length, packet, offset + 1);
            packet[offset] = (byte)written;
            offset += written + 1;
        }

        packet[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), recordType);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), 1);
        offset += 4;

        if (!dnssecOk)
        {
            return packet;
        }

        // Псевдозапись OPT (RFC 6891 §6.1.2): корневое имя, тип 41, «класс» —
        // размер принимаемого ответа, старший бит TTL — DO.
        packet[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), RecordTypeOpt);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), EdnsPayloadSize);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(offset + 4), 0x0000_8000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 8), 0);

        return packet;
    }

    public static DnsResponse Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderLength)
        {
            throw new FormatException("Ответ DNS короче заголовка.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);

        var offset = HeaderLength;

        for (var i = 0; i < questionCount; i++)
        {
            SkipName(packet, ref offset);
            offset += 4;
        }

        var answers = new List<DnsRecord>(answerCount);

        for (var i = 0; i < answerCount && offset < packet.Length; i++)
        {
            var name = ReadName(packet, ref offset);

            if (offset + 10 > packet.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;

            if (offset + dataLength > packet.Length)
            {
                break;
            }

            var value = ReadRecordData(packet, offset, dataLength, type);
            answers.Add(new DnsRecord(name, RecordTypeName(type), ttl, value));

            offset += dataLength;
        }

        return new DnsResponse
        {
            ResponseCode = flags & 0x000F,
            IsAuthoritative = (flags & 0x0400) != 0,
            IsTruncated = (flags & 0x0200) != 0,
            IsAuthenticData = (flags & 0x0020) != 0,
            Answers = answers,
        };
    }

    private static string ReadRecordData(ReadOnlySpan<byte> packet, int offset, int length, ushort type)
    {
        switch (type)
        {
            case RecordTypeA when length == 4:
                return new IPAddress(packet.Slice(offset, 4).ToArray()).ToString();

            case RecordTypeAaaa when length == 16:
                return new IPAddress(packet.Slice(offset, 16).ToArray()).ToString();

            case RecordTypeCname:
            case RecordTypeNs:
            case RecordTypePtr:
            {
                var cursor = offset;
                return ReadName(packet, ref cursor);
            }

            case RecordTypeMx when length > 2:
            {
                var preference = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
                var cursor = offset + 2;
                return $"{preference} {ReadName(packet, ref cursor)}";
            }

            case RecordTypeTxt:
            {
                var builder = new StringBuilder();
                var cursor = offset;
                var end = offset + length;

                while (cursor < end)
                {
                    var chunk = packet[cursor++];
                    if (cursor + chunk > end)
                    {
                        break;
                    }

                    builder.Append(Encoding.UTF8.GetString(packet.Slice(cursor, chunk)));
                    cursor += chunk;
                }

                return builder.ToString();
            }

            // Содержимое подписи не разбираем — проверить её всё равно нечем без цепочки
            // доверия. Разбираем то, что отвечает на вопрос оператора: что подписано,
            // кем и до какого числа подпись годна.
            case RecordTypeRrsig when length > 18:
            {
                var covered = RecordTypeName(BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]));
                var expiration = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 8)..]);
                var cursor = offset + 18;
                var signer = ReadName(packet, ref cursor);

                var until = DateTimeOffset.FromUnixTimeSeconds(expiration)
                    .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

                return $"{covered}, подписал {signer}, годна до {until}";
            }

            default:
                return $"{length} байт";
        }
    }

    /// <summary>
    /// Читает имя, разворачивая сжатие по указателям (RFC 1035 §4.1.4).
    /// </summary>
    /// <remarks>
    /// Число переходов ограничено намеренно: злонамеренный или повреждённый ответ может
    /// содержать указатель на самого себя, и наивный разбор зациклится. Инструмент
    /// диагностики обязан переживать некорректный ответ, а не зависать на нём.
    /// </remarks>
    private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var builder = new StringBuilder();
        var jumps = 0;
        var cursor = offset;
        var jumped = false;

        while (cursor < packet.Length)
        {
            var length = packet[cursor];

            if (length == 0)
            {
                cursor++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length || ++jumps > MaxPointerJumps)
                {
                    break;
                }

                var pointer = ((length & 0x3F) << 8) | packet[cursor + 1];

                if (!jumped)
                {
                    offset = cursor + 2;
                    jumped = true;
                }

                cursor = pointer;
                continue;
            }

            cursor++;

            if (cursor + length > packet.Length)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.ASCII.GetString(packet.Slice(cursor, length)));
            cursor += length;
        }

        if (!jumped)
        {
            offset = cursor;
        }

        return builder.Length == 0 ? "." : builder.ToString();
    }

    private static void SkipName(ReadOnlySpan<byte> packet, ref int offset)
    {
        while (offset < packet.Length)
        {
            var length = packet[offset];

            if (length == 0)
            {
                offset++;
                return;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                return;
            }

            offset += length + 1;
        }
    }
}
