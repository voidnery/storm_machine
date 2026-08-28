using System.Globalization;
using System.Text;
using Lextm.SharpSnmpLib;

namespace StormMachine.Snmp;

/// <summary>
/// Разбор ответов: индекс строки и значение.
/// </summary>
/// <remarks>
/// Таблица в SNMP — это не таблица, а плоский список узлов, где номер строки дописан
/// к идентификатору столбца. Собрать из них строки — работа читающего, и вся она здесь.
/// <para>
/// Индекс не всегда одно число. У портов это <c>ifIndex</c>, у соседей LLDP — тройка
/// «отметка времени, локальный порт, номер соседа», у таблицы пересылки — шесть байт
/// MAC-адреса, а в версии с VLAN ещё и номер VLAN перед ними. Отсюда разные способы
/// достать из хвоста то, что нужно.
/// </para>
/// </remarks>
internal static class SnmpValues
{
    /// <summary>Хвост идентификатора после столбца: то, что кодирует номер строки.</summary>
    public static string Suffix(ObjectIdentifier oid, string column)
    {
        var text = oid.ToString();

        return text.Length > column.Length + 1 && text.StartsWith(column, StringComparison.Ordinal)
            ? text[(column.Length + 1)..]
            : string.Empty;
    }

    /// <summary>Индекс строки, когда он одно число: таблицы портов.</summary>
    public static int? Index(ObjectIdentifier oid, string column)
    {
        var suffix = Suffix(oid, column);

        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : null;
    }

    /// <summary>Части хвоста числами.</summary>
    public static int[] Parts(ObjectIdentifier oid, string column)
    {
        var suffix = Suffix(oid, column);

        if (suffix.Length == 0)
        {
            return [];
        }

        var pieces = suffix.Split('.');
        var parts = new int[pieces.Length];

        for (var i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out parts[i]))
            {
                return [];
            }
        }

        return parts;
    }

    /// <summary>
    /// MAC-адрес из шести последних частей хвоста.
    /// </summary>
    /// <remarks>
    /// Именно последних: в таблице с разбивкой по VLAN перед адресом стоит её номер,
    /// и брать первые шесть было бы ошибкой ровно на тех устройствах, ради которых
    /// вторая таблица и читается.
    /// </remarks>
    public static string? MacFromTail(int[] parts)
    {
        if (parts.Length < 6)
        {
            return null;
        }

        var builder = new StringBuilder(17);

        for (var i = parts.Length - 6; i < parts.Length; i++)
        {
            if (parts[i] is < 0 or > 255)
            {
                return null;
            }

            if (builder.Length > 0)
            {
                builder.Append('-');
            }

            builder.Append(parts[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public static string Text(ISnmpData data) => data switch
    {
        OctetString text => Printable(text),
        null => string.Empty,
        _ => data.ToString() ?? string.Empty,
    };

    public static long Number(ISnmpData data) => data switch
    {
        Integer32 value => value.ToInt32(),
        Counter32 value => value.ToUInt32(),
        Gauge32 value => value.ToUInt32(),
        TimeTicks value => value.ToUInt32(),
        Counter64 value => unchecked((long)value.ToUInt64()),
        _ => 0,
    };

    public static TimeSpan Ticks(ISnmpData data) => data is TimeTicks ticks
        ? ticks.ToTimeSpan()
        : TimeSpan.Zero;

    /// <summary>Физический адрес: шесть байт, а не строка.</summary>
    public static string? Mac(ISnmpData data)
    {
        if (data is not OctetString octets)
        {
            return null;
        }

        var raw = octets.GetRaw();

        if (raw.Length != 6)
        {
            return null;
        }

        return string.Join('-', raw.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>Как назвать тип значения человеку.</summary>
    public static string TypeName(ISnmpData data) => data?.TypeCode.ToString() ?? "Null";

    /// <summary>Строгий UTF-8: неверная последовательность — исключение, а не подмена.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Строка, пригодная для показа.
    /// </summary>
    /// <remarks>
    /// Строки в SNMP — это байты, и в них попадает что угодно: идентификатор шасси
    /// бывает шестью байтами MAC-адреса, а описание порта — с переводами строк.
    /// Непечатаемое показывается шестнадцатеричным, иначе консоль ломается,
    /// а оператор видит мусор вместо ответа.
    /// <para>
    /// Кодировка — UTF-8, как предписывает <c>SnmpAdminString</c> (RFC 3411 §5).
    /// Это не мелочь: <c>sysLocation</c> и подписи портов на объектах пишут
    /// по-русски, и разбор их как ASCII превращает «серверная, стойка 2» в ряд
    /// вопросительных знаков. Байты, которые в UTF-8 не укладываются, читаются
    /// как Latin-1: старое оборудование пишет в местной однобайтовой кодировке,
    /// и хотя угадать её нечем, сохранить байты лучше, чем потерять строку целиком.
    /// </para>
    /// </remarks>
    private static string Printable(OctetString text)
    {
        var raw = text.GetRaw();

        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var printable = Array.TrueForAll(raw, b => b >= 0x20 || b is 0x09 or 0x0A or 0x0D);

        if (!printable)
        {
            return string.Join('-', raw.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        }

        string decoded;

        try
        {
            decoded = StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            decoded = Encoding.Latin1.GetString(raw);
        }

        return decoded.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
