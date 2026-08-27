using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StormMachine.Platform.Geo;

/// <summary>
/// Читатель баз в формате MaxMind DB (MMDB).
/// </summary>
/// <remarks>
/// Формат выбран потому, что в нём распространяется <b>DB-IP Lite</b> — единственная
/// пригодная база принадлежности адресов, которую можно свободно использовать
/// в открытом продукте (CC BY-SA 4.0, требуется указание источника).
/// <para>
/// Реализован свой читатель, а не взята библиотека: формат маленький и полностью
/// описан, а сторонняя зависимость тянула бы за собой чужую лицензию и чужие обновления
/// ради двух сотен строк разбора. То же решение и по той же причине, что с разбором
/// DNS в <c>DnsWire</c>.
/// </para>
/// </remarks>
internal sealed class MaxMindDatabase
{
    /// <summary>Маркер начала раздела метаданных — по нему он и ищется с конца файла.</summary>
    private static readonly byte[] MetadataMarker =
        [0xAB, 0xCD, 0xEF, .. "MaxMind.com"u8.ToArray()];

    /// <summary>Спецификация ограничивает раздел метаданных 128 КБ от конца файла.</summary>
    private const int MetadataSearchWindow = 128 * 1024;

    /// <summary>Разделитель дерева и данных: шестнадцать нулевых байт.</summary>
    private const int DataSectionSeparator = 16;

    /// <summary>Предел размера файла: защита от попытки открыть не то.</summary>
    private const long MaxFileSizeBytes = 512L * 1024 * 1024;

    private readonly byte[] _data;
    private readonly int _nodeCount;
    private readonly int _nodeByteSize;
    private readonly int _recordSize;
    private readonly int _searchTreeSize;
    private readonly int _dataSectionStart;
    private readonly int _ipVersion;

    private MaxMindDatabase(byte[] data, IReadOnlyDictionary<string, object?> metadata)
    {
        _data = data;

        _nodeCount = (int)RequireUInt(metadata, "node_count");
        _recordSize = (int)RequireUInt(metadata, "record_size");
        _ipVersion = (int)RequireUInt(metadata, "ip_version");

        if (_recordSize is not (24 or 28 or 32))
        {
            throw new InvalidDataException($"Неподдерживаемый размер записи MMDB: {_recordSize}.");
        }

        if (_ipVersion is not (4 or 6))
        {
            throw new InvalidDataException($"Неподдерживаемая версия адресов MMDB: {_ipVersion}.");
        }

        _nodeByteSize = _recordSize * 2 / 8;
        _searchTreeSize = _nodeCount * _nodeByteSize;
        _dataSectionStart = _searchTreeSize + DataSectionSeparator;

        if (_dataSectionStart >= data.Length)
        {
            throw new InvalidDataException("Дерево поиска MMDB не помещается в файл.");
        }

        DatabaseType = metadata.TryGetValue("database_type", out var type) ? type as string ?? "unknown" : "unknown";
        BuildTime = metadata.TryGetValue("build_epoch", out var epoch) && epoch is long seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    public string DatabaseType { get; }

    public DateTimeOffset? BuildTime { get; }

    public string Describe() => BuildTime is { } built
        ? $"{DatabaseType} от {built.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
        : DatabaseType;

    public static MaxMindDatabase Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);

        if (!info.Exists)
        {
            throw new FileNotFoundException("База MMDB не найдена.", path);
        }

        if (info.Length > MaxFileSizeBytes)
        {
            throw new InvalidDataException($"Файл {path} слишком велик для базы MMDB ({info.Length} байт).");
        }

        return Load(File.ReadAllBytes(path));
    }

    internal static MaxMindDatabase Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var metadataStart = FindMetadata(data);

        if (metadataStart < 0)
        {
            throw new InvalidDataException("В файле нет маркера MaxMind.com — это не база MMDB.");
        }

        // Указатели внутри метаданных отсчитываются от начала самого раздела метаданных,
        // а не от раздела данных: это отдельное правило формата, и его легко пропустить.
        var reader = new Decoder(data, metadataStart);
        var offset = metadataStart;

        return Decoder.AsMap(reader.Read(ref offset)) is { } metadata
            ? new MaxMindDatabase(data, metadata)
            : throw new InvalidDataException("Раздел метаданных MMDB не является отображением.");
    }

    /// <summary>Ищет данные для адреса. <c>null</c> — адреса нет в базе.</summary>
    public IReadOnlyDictionary<string, object?>? Lookup(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var bytes = Normalize(address);

        if (bytes is null)
        {
            return null;
        }

        var node = 0;
        var bitCount = bytes.Length * 8;

        for (var i = 0; i < bitCount && node < _nodeCount; i++)
        {
            var bit = (bytes[i >> 3] >> (7 - (i & 7))) & 1;
            node = ReadRecord(node, bit);
        }

        if (node <= _nodeCount)
        {
            // Равно числу узлов — явная отметка «данных нет». Меньше — дерево оборвалось
            // раньше, чем кончились биты: такой файл повреждён, но падать из-за него
            // посреди трассировки незачем.
            return null;
        }

        var offset = node - _nodeCount - DataSectionSeparator + _dataSectionStart;

        if (offset < _dataSectionStart || offset >= _data.Length)
        {
            return null;
        }

        var decoder = new Decoder(_data, _dataSectionStart);

        return Decoder.AsMap(decoder.Read(ref offset));
    }

    /// <summary>
    /// Приводит адрес к виду, в котором его хранит база.
    /// </summary>
    /// <remarks>
    /// Базы DB-IP шестнадцатибайтовые: адреса IPv4 лежат в них в отображённом виде
    /// <c>::ffff:0:0/96</c>. Без этого преобразования обычный IPv4 не находился бы вовсе.
    /// </remarks>
    private byte[]? Normalize(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return _ipVersion == 4
                ? address.GetAddressBytes()
                : address.MapToIPv6().GetAddressBytes();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        // База только для IPv4 ничего не знает про IPv6 — кроме отображённых адресов.
        if (_ipVersion == 4)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().GetAddressBytes() : null;
        }

        return address.GetAddressBytes();
    }

    private int ReadRecord(int node, int bit)
    {
        var at = node * _nodeByteSize;

        return _recordSize switch
        {
            24 => bit == 0 ? ReadUInt24(at) : ReadUInt24(at + 3),

            // Половинки 28-битной записи хранятся в общем среднем байте: старшие четыре
            // бита принадлежат левой записи, младшие — правой.
            28 => bit == 0
                ? ((_data[at + 3] >> 4) << 24) | ReadUInt24(at)
                : ((_data[at + 3] & 0x0F) << 24) | ReadUInt24(at + 4),

            _ => (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(bit == 0 ? at : at + 4, 4)),
        };
    }

    private int ReadUInt24(int at) => (_data[at] << 16) | (_data[at + 1] << 8) | _data[at + 2];

    private static int FindMetadata(byte[] data)
    {
        var marker = MetadataMarker;
        var from = Math.Max(0, data.Length - MetadataSearchWindow);

        for (var i = data.Length - marker.Length; i >= from; i--)
        {
            if (data.AsSpan(i, marker.Length).SequenceEqual(marker))
            {
                return i + marker.Length;
            }
        }

        return -1;
    }

    private static ulong RequireUInt(IReadOnlyDictionary<string, object?> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && value is long number && number >= 0
            ? (ulong)number
            : throw new InvalidDataException($"В метаданных MMDB нет поля {key}.");

    /// <summary>
    /// Разбор значений раздела данных MMDB.
    /// </summary>
    /// <remarks>
    /// Формат самоописывающийся: управляющий байт задаёт тип и размер, а составные
    /// значения вкладываются друг в друга. Указатели ссылаются на уже разобранные
    /// значения — так база не повторяет одинаковые названия организаций миллион раз.
    /// </remarks>
    private readonly struct Decoder(byte[] data, int sectionStart)
    {
        private const int MaxDepth = 32;

        private readonly byte[] _data = data;
        private readonly int _sectionStart = sectionStart;

        public object? Read(ref int offset) => Read(ref offset, 0);

        public static IReadOnlyDictionary<string, object?>? AsMap(object? value) =>
            value as IReadOnlyDictionary<string, object?>;

        private object? Read(ref int offset, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidDataException("Слишком глубокая вложенность значений MMDB.");
            }

            if (offset < 0 || offset >= _data.Length)
            {
                throw new InvalidDataException("Смещение вне файла MMDB.");
            }

            var control = _data[offset++];
            var type = control >> 5;

            if (type == 0)
            {
                type = _data[offset++] + 7;
            }

            if (type == 1)
            {
                return ReadPointer(control, ref offset, depth);
            }

            var size = ReadSize(control & 0x1F, ref offset);

            return type switch
            {
                2 => ReadString(ref offset, size),
                3 => ReadDouble(ref offset, size),
                4 => ReadBytes(ref offset, size),
                5 or 6 or 9 or 10 => (long)ReadUnsigned(ref offset, size),
                7 => ReadMap(ref offset, size, depth),
                8 => ReadSigned(ref offset, size),
                11 => ReadArray(ref offset, size, depth),
                14 => size != 0,
                15 => ReadFloat(ref offset, size),

                // Контейнер кэша и маркер конца в базах DB-IP не встречаются,
                // но пропустить их дешевле, чем упасть на чужой базе.
                12 or 13 => null,

                _ => throw new InvalidDataException($"Неизвестный тип значения MMDB: {type}."),
            };
        }

        private object? ReadPointer(byte control, ref int offset, int depth)
        {
            var kind = (control >> 3) & 0x03;
            var value = control & 0x07;

            var target = kind switch
            {
                0 => (value << 8) | _data[offset++],
                1 => ((value << 16) | (int)ReadUnsignedAt(ref offset, 2)) + 2048,
                2 => ((value << 24) | (int)ReadUnsignedAt(ref offset, 3)) + 526336,
                _ => (int)ReadUnsignedAt(ref offset, 4),
            };

            var absolute = _sectionStart + target;

            return Read(ref absolute, depth + 1);
        }

        private int ReadSize(int size, ref int offset) => size switch
        {
            29 => 29 + _data[offset++],
            30 => 285 + (int)ReadUnsignedAt(ref offset, 2),
            31 => 65821 + (int)ReadUnsignedAt(ref offset, 3),
            _ => size,
        };

        private string ReadString(ref int offset, int size)
        {
            var value = Encoding.UTF8.GetString(_data, offset, size);
            offset += size;
            return value;
        }

        private byte[] ReadBytes(ref int offset, int size)
        {
            var value = _data.AsSpan(offset, size).ToArray();
            offset += size;
            return value;
        }

        private double ReadDouble(ref int offset, int size)
        {
            if (size != 8)
            {
                throw new InvalidDataException($"Неверный размер double в MMDB: {size}.");
            }

            var value = BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan(offset, 8));
            offset += 8;
            return value;
        }

        private double ReadFloat(ref int offset, int size)
        {
            if (size != 4)
            {
                throw new InvalidDataException($"Неверный размер float в MMDB: {size}.");
            }

            var value = BinaryPrimitives.ReadSingleBigEndian(_data.AsSpan(offset, 4));
            offset += 4;
            return value;
        }

        private ulong ReadUnsigned(ref int offset, int size)
        {
            if (size > 8)
            {
                // Стодвадцативосьмибитные числа в базах принадлежности не встречаются;
                // берутся младшие восемь байт, чтобы не тащить BigInteger ради ничего.
                offset += size - 8;
                size = 8;
            }

            return ReadUnsignedAt(ref offset, size);
        }

        private ulong ReadUnsignedAt(ref int offset, int size)
        {
            ulong value = 0;

            for (var i = 0; i < size; i++)
            {
                value = (value << 8) | _data[offset + i];
            }

            offset += size;
            return value;
        }

        private long ReadSigned(ref int offset, int size)
        {
            if (size == 0)
            {
                return 0;
            }

            var raw = (long)ReadUnsignedAt(ref offset, size);
            var bits = size * 8;

            // Дополнительный код записан в size байтах — старший бит расширяется вручную.
            return bits < 64 && (raw & (1L << (bits - 1))) != 0
                ? raw - (1L << bits)
                : raw;
        }

        private Dictionary<string, object?> ReadMap(ref int offset, int size, int depth)
        {
            var map = new Dictionary<string, object?>(size, StringComparer.Ordinal);

            for (var i = 0; i < size; i++)
            {
                if (Read(ref offset, depth + 1) is not string key)
                {
                    throw new InvalidDataException("Ключ отображения MMDB не является строкой.");
                }

                map[key] = Read(ref offset, depth + 1);
            }

            return map;
        }

        private List<object?> ReadArray(ref int offset, int size, int depth)
        {
            var items = new List<object?>(size);

            for (var i = 0; i < size; i++)
            {
                items.Add(Read(ref offset, depth + 1));
            }

            return items;
        }
    }
}
