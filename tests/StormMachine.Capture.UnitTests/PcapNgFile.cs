using System.Buffers.Binary;

namespace StormMachine.Capture.UnitTests;

/// <summary>
/// Сборка pcapng байт за байтом.
/// </summary>
/// <remarks>
/// Намеренно вручную, по описанию формата, а не той же программой, которая файлы
/// читает. Файл, собранный и разобранный одним кодом, проверяет его согласованность
/// с самим собой; ошибку в <b>нашем</b> понимании формата — перепутанный порядок
/// байтов, забытое выравнивание опции, не та доля секунды — он не поймает.
/// <para>
/// Раскладка взята из черновика <c>draft-tuexen-opsawg-pcapng</c>: блок — это тип,
/// длина, тело и та же длина ещё раз.
/// </para>
/// </remarks>
internal static class PcapNgFile
{
    internal const byte Microseconds = 6;

    internal const byte Nanoseconds = 9;

    /// <summary>Файл с одним интерфейсом и перечисленными кадрами.</summary>
    internal static byte[] With(byte resolution, params (byte[] Bytes, ulong Stamp)[] frames)
    {
        var file = new List<byte>();

        file.AddRange(SectionHeader());
        file.AddRange(Interface(resolution));

        foreach (var (bytes, stamp) in frames)
        {
            file.AddRange(Packet(bytes, stamp));
        }

        return [.. file];
    }

    /// <summary>Заголовок секции: по нему читатель узнаёт порядок байтов.</summary>
    internal static byte[] SectionHeader()
    {
        var body = new List<byte>();

        body.AddRange(Four(0x1A2B3C4D));
        body.AddRange(Two(1));
        body.AddRange(Two(0));

        // Длина секции неизвестна — так и записывается.
        body.AddRange(Four(0xFFFFFFFF));
        body.AddRange(Four(0xFFFFFFFF));

        return Block(0x0A0D0D0A, body);
    }

    /// <summary>Описание интерфейса: Ethernet и разрешение отметок времени.</summary>
    internal static byte[] Interface(byte resolution)
    {
        var body = new List<byte>();

        body.AddRange(Two(1));
        body.AddRange(Two(0));
        body.AddRange(Four(0));

        // Опция 9 — доля секунды; значение выравнивается до четырёх байтов.
        body.AddRange(Two(9));
        body.AddRange(Two(1));
        body.AddRange([resolution, 0, 0, 0]);

        // Конец опций.
        body.AddRange(Two(0));
        body.AddRange(Two(0));

        return Block(0x00000001, body);
    }

    internal static byte[] Packet(byte[] frame, ulong stamp) => Packet(frame, stamp, frame.Length);

    /// <summary>Кадр с объявленной длиной — она может и соврать: так проверяется стойкость.</summary>
    internal static byte[] Packet(byte[] frame, ulong stamp, int declared)
    {
        var body = new List<byte>();

        body.AddRange(Four(0));
        body.AddRange(Four((uint)(stamp >> 32)));
        body.AddRange(Four((uint)(stamp & 0xFFFFFFFF)));
        body.AddRange(Four((uint)declared));
        body.AddRange(Four((uint)declared));
        body.AddRange(frame);

        while (body.Count % 4 != 0)
        {
            body.Add(0);
        }

        return Block(0x00000006, body);
    }

    private static byte[] Block(uint type, List<byte> body)
    {
        var total = (uint)(body.Count + 12);
        var block = new List<byte>();

        block.AddRange(Four(type));
        block.AddRange(Four(total));
        block.AddRange(body);
        block.AddRange(Four(total));

        return [.. block];
    }

    private static byte[] Four(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);

        return bytes;
    }

    private static byte[] Two(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);

        return bytes;
    }
}
