using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace StormMachine.Platform.UnitTests;

/// <summary>
/// Собирает крошечную базу формата MaxMind DB для проверок читателя.
/// </summary>
/// <remarks>
/// Настоящую базу DB-IP в репозиторий класть нельзя: у неё своя лицензия и вес в мегабайты.
/// Поэтому тесты работают на синтетической базе, собранной здесь по той же спецификации.
/// <para>
/// Писатель намеренно самый простой из возможных: записи по 32 бита, никаких указателей
/// в разделе данных. Он проверяет читатель, а не соревнуется с ним в компактности —
/// и если оба ошибутся одинаково, тест это не поймает, поэтому значения ниже
/// сверены со спецификацией формата, а не выведены из кода читателя.
/// </para>
/// </remarks>
internal sealed class MaxMindDbWriter
{
    /// <summary>Запись в 32 бита: узел — восемь байт, левая и правая половины по четыре.</summary>
    private const int RecordSize = 32;

    private const int NodeByteSize = RecordSize * 2 / 8;

    private const int DataSectionSeparator = 16;

    /// <summary>Пустая запись: заполняется числом узлов при сборке.</summary>
    private const long Empty = -1;

    /// <summary>Ссылка на запись данных с номером <c>j</c> кодируется как <c>DataBase - j</c>.</summary>
    private const long DataBase = -1000;

    private readonly List<long[]> _nodes = [[Empty, Empty]];
    private readonly List<Dictionary<string, object?>> _data = [];
    private readonly int _ipVersion;

    public MaxMindDbWriter(int ipVersion = 6) => _ipVersion = ipVersion;

    public string DatabaseType { get; set; } = "test-asn";

    /// <summary>
    /// Добавляет сеть. Для базы IPv6 адреса IPv4 кладутся отображёнными в <c>::ffff:0:0/96</c>,
    /// ровно как это делает DB-IP.
    /// </summary>
    public void Add(string network, int prefixLength, Dictionary<string, object?> data)
    {
        var address = IPAddress.Parse(network);

        if (_ipVersion == 6 && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            address = address.MapToIPv6();
            prefixLength += 96;
        }

        var bytes = address.GetAddressBytes();
        var dataIndex = _data.Count;
        _data.Add(data);

        var node = 0;

        for (var i = 0; i < prefixLength; i++)
        {
            var bit = (bytes[i >> 3] >> (7 - (i & 7))) & 1;
            var last = i == prefixLength - 1;

            if (last)
            {
                if (_nodes[node][bit] >= 0)
                {
                    throw new InvalidOperationException(
                        $"Сеть {network}/{prefixLength} шире уже добавленной. "
                        + "Писатель тестов требует порядка от широких сетей к узким.");
                }

                _nodes[node][bit] = DataBase - dataIndex;
                return;
            }

            var record = _nodes[node][bit];

            if (record == Empty)
            {
                _nodes.Add([Empty, Empty]);
                _nodes[node][bit] = _nodes.Count - 1;
                node = _nodes.Count - 1;
            }
            else if (record >= 0)
            {
                node = (int)record;
            }
            else
            {
                // На пути лежат данные более короткого префикса. Запись не может быть
                // одновременно данными и узлом, поэтому она разворачивается в узел,
                // обе половины которого наследуют те же данные. Так более длинный
                // префикс получает свой лист, а всё остальное продолжает отвечать
                // прежней записью — ровно то, что означает longest prefix match.
                _nodes.Add([record, record]);
                _nodes[node][bit] = _nodes.Count - 1;
                node = _nodes.Count - 1;
            }
        }
    }

    public byte[] Build()
    {
        var dataSection = new MemoryStream();
        var offsets = new int[_data.Count];

        for (var i = 0; i < _data.Count; i++)
        {
            offsets[i] = (int)dataSection.Length;
            WriteValue(dataSection, _data[i]);
        }

        var nodeCount = _nodes.Count;
        var tree = new byte[nodeCount * NodeByteSize];

        for (var i = 0; i < nodeCount; i++)
        {
            for (var side = 0; side < 2; side++)
            {
                var record = _nodes[i][side];

                var value = record switch
                {
                    Empty => (uint)nodeCount,
                    >= 0 => (uint)record,

                    // Смещение данных кодируется как node_count + 16 + смещение
                    // внутри раздела данных — это и есть то место, где формат
                    // легче всего понять неправильно.
                    _ => (uint)(nodeCount + DataSectionSeparator + offsets[(int)(DataBase - record)]),
                };

                BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan((i * NodeByteSize) + (side * 4), 4), value);
            }
        }

        var file = new MemoryStream();
        file.Write(tree);
        file.Write(new byte[DataSectionSeparator]);
        dataSection.Position = 0;
        dataSection.CopyTo(file);

        file.Write([0xAB, 0xCD, 0xEF]);
        file.Write("MaxMind.com"u8);

        WriteValue(file, new Dictionary<string, object?>
        {
            ["node_count"] = (uint)nodeCount,
            ["record_size"] = (ushort)RecordSize,
            ["ip_version"] = (ushort)_ipVersion,
            ["database_type"] = DatabaseType,
            ["binary_format_major_version"] = (ushort)2,
            ["binary_format_minor_version"] = (ushort)0,
            ["build_epoch"] = (ulong)1_767_225_600,
        });

        return file.ToArray();
    }

    private static void WriteValue(Stream stream, object? value)
    {
        switch (value)
        {
            case string text:
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                WriteControl(stream, type: 2, bytes.Length);
                stream.Write(bytes);
                return;
            }

            case ushort number:
                WriteUnsigned(stream, type: 5, number, maxBytes: 2);
                return;

            case uint number:
                WriteUnsigned(stream, type: 6, number, maxBytes: 4);
                return;

            case ulong number:
                WriteUnsigned(stream, type: 9, number, maxBytes: 8);
                return;

            case Dictionary<string, object?> map:
            {
                WriteControl(stream, type: 7, map.Count);

                foreach (var (key, item) in map)
                {
                    WriteValue(stream, key);
                    WriteValue(stream, item);
                }

                return;
            }

            default:
                throw new NotSupportedException($"Тип {value?.GetType().Name ?? "null"} писателю не нужен.");
        }
    }

    private static void WriteUnsigned(Stream stream, int type, ulong value, int maxBytes)
    {
        // Ведущие нули не пишутся — размер и есть число значащих байт.
        var bytes = new byte[maxBytes];

        for (var i = 0; i < maxBytes; i++)
        {
            bytes[maxBytes - 1 - i] = (byte)(value >> (i * 8));
        }

        var start = 0;
        while (start < maxBytes && bytes[start] == 0)
        {
            start++;
        }

        var size = maxBytes - start;
        WriteControl(stream, type, size);
        stream.Write(bytes, start, size);
    }

    /// <summary>
    /// Пишет управляющий байт: три старших бита — тип, пять младших — размер.
    /// </summary>
    /// <remarks>
    /// Два места, где формат легко понять неправильно, и оба встречаются в этой базе.
    /// <para>
    /// Типы больше семи в три бита не помещаются: поле типа обнуляется, а настоящий тип
    /// уходит в отдельный байт сразу за управляющим — до байтов продолжения размера,
    /// а не после. Так записан <c>build_epoch</c> (тип 9).
    /// </para>
    /// <para>
    /// Размеры от 29 кодируются продолжением: 29, 30 и 31 в поле размера означают
    /// «читай ещё один, два или три байта». Ключ <c>autonomous_system_organization</c>
    /// длиной в тридцать байт попадает ровно в первый из этих случаев.
    /// </para>
    /// </remarks>
    private static void WriteControl(Stream stream, int type, int size)
    {
        int sizeField;
        byte[] sizeTail;

        switch (size)
        {
            case < 29:
                sizeField = size;
                sizeTail = [];
                break;

            case < 29 + 256:
                sizeField = 29;
                sizeTail = [(byte)(size - 29)];
                break;

            case < 285 + 65536:
                sizeField = 30;
                sizeTail = [(byte)((size - 285) >> 8), (byte)(size - 285)];
                break;

            default:
                throw new NotSupportedException("Писателю тестов такие длинные значения не нужны.");
        }

        stream.WriteByte((byte)(((type <= 7 ? type : 0) << 5) | sizeField));

        if (type > 7)
        {
            stream.WriteByte((byte)(type - 7));
        }

        stream.Write(sizeTail);
    }
}
