using System.Buffers.Binary;

namespace StormMachine.Capture.Frames;

/// <summary>Кадр, вычитанный из файла захвата.</summary>
public sealed record CapturedFrame(byte[] Bytes, DateTimeOffset ObservedUtc);

/// <summary>
/// Чтение pcapng.
/// </summary>
/// <remarks>
/// Нужно затем, что встроенный в Windows <c>pktmon</c> не отдаёт кадры потоком:
/// он пишет журнал, а <c>pktmon etl2pcap</c> превращает его в pcapng. Формат описан
/// в черновике IETF <c>draft-tuexen-opsawg-pcapng</c> и читается без библиотек —
/// блок, длина, тело, та же длина ещё раз.
/// <para>
/// Читается ровно то, что нужно продукту: блоки кадров и разрешение времени
/// интерфейса. Блоки статистики, комментарии и записи имён хостов пропускаются —
/// не потому, что они неинтересны, а потому, что непрочитанный блок здесь
/// обходится в одну строку, а прочитанный и неиспользуемый — в целый раздел,
/// который однажды разойдётся с правдой.
/// </para>
/// <para>
/// Блок «простого пакета» (SPB) не поддержан намеренно: в нём нет времени, а время
/// кадра продукту нужно — по нему строится «когда этот сервер DHCP появился».
/// pktmon его и не пишет.
/// </para>
/// </remarks>
public static class PcapNgReader
{
    private const uint SectionHeader = 0x0A0D0D0A;
    private const uint InterfaceDescription = 0x00000001;
    private const uint EnhancedPacket = 0x00000006;

    /// <summary>Порядок байтов секции: по этому числу он и опознаётся.</summary>
    private const uint ByteOrderMagic = 0x1A2B3C4D;

    /// <summary>Опция интерфейса: разрешение отметок времени.</summary>
    private const ushort TimestampResolution = 9;

    /// <summary>Разрешение по умолчанию — микросекунды (10⁻⁶).</summary>
    private const byte DefaultResolution = 6;

    /// <summary>
    /// Предел на один кадр.
    /// </summary>
    /// <remarks>
    /// Испорченный файл может объявить кадр в гигабайт, и доверять этому числу
    /// нельзя: оно пришло с диска, а не из нашего кода. Самый большой кадр,
    /// который бывает в сети, — девять килобайт (jumbo); берём вдвое с запасом.
    /// </remarks>
    private const int FrameLimit = 64 * 1024;

    public static IEnumerable<CapturedFrame> ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);

        foreach (var frame in Read(stream))
        {
            yield return frame;
        }
    }

    /// <summary>
    /// Кадры в порядке записи.
    /// </summary>
    /// <remarks>
    /// Обрывается молча, если файл кончился на середине блока: журнал мог быть
    /// прерван на полуслове — остановкой захвата, нехваткой места, выключением.
    /// Прочитанное до обрыва остаётся правдой, и ронять из-за хвоста весь разбор
    /// значило бы терять настоящие кадры из-за ненастоящей беды.
    /// </remarks>
    public static IEnumerable<CapturedFrame> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[8];
        var magic = new byte[4];
        var tail = new byte[4];

        // Разрешение времени у каждого интерфейса своё; до первого IDB берём
        // принятое по умолчанию, иначе первый же кадр уехал бы в 1970-й.
        var resolutions = new List<byte>();
        var swapped = false;

        while (Fill(stream, header))
        {
            // Тип заголовка секции читается одинаково в обоих порядках байтов:
            // 0A 0D 0D 0A перевёрнутый — он же. Так в формате и задумано: иначе
            // порядок байтов было бы неоткуда узнать.
            var type = Read32(header, 0, swapped);

            if (type == SectionHeader)
            {
                if (!Fill(stream, magic))
                {
                    yield break;
                }

                // Новая секция — свой порядок байтов и свой счёт интерфейсов.
                swapped = BinaryPrimitives.ReadUInt32LittleEndian(magic) != ByteOrderMagic;
                resolutions.Clear();

                var length = Read32(header, 4, swapped);

                // Тело секции за вычетом уже прочитанного числа порядка байтов.
                if (length < 16 || length > int.MaxValue || !Skip(stream, (int)length - 16, tail))
                {
                    yield break;
                }

                continue;
            }

            var total = Read32(header, 4, swapped);

            if (total < 12 || total > int.MaxValue)
            {
                yield break;
            }

            // Тело: за вычетом типа, длины в начале и её повтора в конце.
            var body = new byte[(int)total - 12];

            if (!Fill(stream, body) || !Fill(stream, tail))
            {
                yield break;
            }

            if (type == InterfaceDescription)
            {
                resolutions.Add(Resolution(body, swapped));
            }
            else if (type == EnhancedPacket && body.Length >= 20)
            {
                var frame = Frame(body, resolutions, swapped);

                if (frame is not null)
                {
                    yield return frame;
                }
            }
        }
    }

    private static CapturedFrame? Frame(byte[] body, List<byte> resolutions, bool swapped)
    {
        var interfaceId = (int)Read32(body, 0, swapped);
        var high = Read32(body, 4, swapped);
        var low = Read32(body, 8, swapped);
        var captured = (int)Read32(body, 12, swapped);

        if (captured <= 0 || captured > FrameLimit || captured > body.Length - 20)
        {
            return null;
        }

        var bytes = new byte[captured];
        Array.Copy(body, 20, bytes, 0, captured);

        var resolution = interfaceId >= 0 && interfaceId < resolutions.Count
            ? resolutions[interfaceId]
            : DefaultResolution;

        return new CapturedFrame(bytes, Moment(((ulong)high << 32) | low, resolution));
    }

    /// <summary>
    /// Отметка времени кадра.
    /// </summary>
    /// <remarks>
    /// В файле она лежит целым числом долей секунды, а какая это доля — сказано
    /// у интерфейса. Старший бит разрешения означает степень двойки вместо десяти;
    /// такие файлы редки, но правило в формате есть, и считать его «неважным»
    /// значит однажды показать время с ошибкой в тысячи раз.
    /// </remarks>
    private static DateTimeOffset Moment(ulong units, byte resolution)
    {
        var ticks = (resolution & 0x80) != 0
            ? (long)(units * (ulong)TimeSpan.TicksPerSecond >> (resolution & 0x7F))
            : FromDecimal(units, resolution);

        return ticks < 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.UnixEpoch.AddTicks(ticks);
    }

    private static long FromDecimal(ulong units, byte resolution)
    {
        // Тик — стомиллионная доля секунды. При разрешении мельче тика лишнее
        // отбрасывается: показать наносекунду продукт всё равно не берётся.
        var perSecond = 1UL;

        for (var i = 0; i < Math.Min(resolution, (byte)19); i++)
        {
            perSecond *= 10;
        }

        return perSecond <= TimeSpan.TicksPerSecond
            ? (long)(units * ((ulong)TimeSpan.TicksPerSecond / perSecond))
            : (long)(units / (perSecond / (ulong)TimeSpan.TicksPerSecond));
    }

    private static byte Resolution(byte[] body, bool swapped)
    {
        // Тело IDB: тип канала (2), резерв (2), длина снимка (4), дальше опции.
        var offset = 8;

        while (offset + 4 <= body.Length)
        {
            var code = Read16(body, offset, swapped);
            var length = Read16(body, offset + 2, swapped);

            if (code == 0)
            {
                break;
            }

            if (code == TimestampResolution && length >= 1 && offset + 4 < body.Length)
            {
                return body[offset + 4];
            }

            // Значение опции выравнивается до четырёх байтов.
            offset += 4 + ((length + 3) / 4 * 4);
        }

        return DefaultResolution;
    }

    private static uint Read32(byte[] source, int offset, bool swapped) => swapped
        ? BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset))
        : BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset));

    private static ushort Read16(byte[] source, int offset, bool swapped) => swapped
        ? BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(offset))
        : BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset));

    /// <summary>Пропустить остаток блока вместе с повтором длины в конце.</summary>
    private static bool Skip(Stream stream, int count, byte[] tail)
    {
        if (count > 0)
        {
            var rest = new byte[count];

            if (!Fill(stream, rest))
            {
                return false;
            }
        }

        return Fill(stream, tail);
    }

    private static bool Fill(Stream stream, byte[] buffer)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var got = stream.Read(buffer, read, buffer.Length - read);

            if (got <= 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }
}
