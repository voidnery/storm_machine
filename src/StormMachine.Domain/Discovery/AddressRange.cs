using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace StormMachine.Domain.Discovery;

/// <summary>
/// Диапазон адресов IPv4 для сканирования.
/// </summary>
/// <remarks>
/// Отдельный тип, а не пара строк, потому что у диапазона есть обязанность:
/// он должен уметь сказать, сколько адресов затронет. Сканирование — активное действие
/// по чужой сети, и оператор обязан видеть его объём <b>до</b> запуска, а не узнавать
/// по факту (требование раздела «Этика» в README).
/// </remarks>
public sealed record AddressRange
{
    /// <summary>
    /// Предел, выше которого диапазон считается неосторожным.
    /// </summary>
    /// <remarks>
    /// Шестнадцать бит — это 65 536 адресов и минуты работы. Больше почти наверняка
    /// означает опечатку в маске, а не намерение: такой диапазон отклоняется,
    /// а не выполняется молча.
    /// </remarks>
    public const int MaxHostCount = 65_536;

    private readonly uint _first;
    private readonly uint _last;

    private AddressRange(uint first, uint last, string text)
    {
        _first = first;
        _last = last;
        Text = text;
    }

    /// <summary>Исходная запись диапазона — она же показывается оператору.</summary>
    public string Text { get; }

    public IPAddress First => FromUInt32(_first);

    public IPAddress Last => FromUInt32(_last);

    public int Count => (int)(_last - _first + 1);

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = ToUInt32(address);

        return value >= _first && value <= _last;
    }

    public IEnumerable<IPAddress> Enumerate()
    {
        for (var value = _first; ; value++)
        {
            yield return FromUInt32(value);

            if (value == _last)
            {
                yield break;
            }
        }
    }

    /// <summary>Разбирает <c>192.168.1.0/24</c> или <c>192.168.1.10-192.168.1.40</c>.</summary>
    public static AddressRange Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var trimmed = text.Trim();

        return trimmed.Contains('/', StringComparison.Ordinal)
            ? ParseCidr(trimmed)
            : trimmed.Contains('-', StringComparison.Ordinal)
                ? ParseSpan(trimmed)
                : ParseSingle(trimmed);
    }

    /// <summary>Подсеть, в которой стоит указанный адрес с указанной длиной префикса.</summary>
    public static AddressRange FromInterface(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Сканирование поддерживает только IPv4.", nameof(address));
        }

        return FromPrefix(ToUInt32(address), prefixLength);
    }

    private static AddressRange ParseCidr(string text)
    {
        var parts = text.Split('/', 2);

        if (!IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix))
        {
            throw new FormatException($"Диапазон «{text}» не разобран. Ожидается вид 192.168.1.0/24.");
        }

        return FromPrefix(ToUInt32(address), prefix);
    }

    private static AddressRange ParseSpan(string text)
    {
        var parts = text.Split('-', 2);

        if (!IPAddress.TryParse(parts[0].Trim(), out var from)
            || !IPAddress.TryParse(parts[1].Trim(), out var to)
            || from.AddressFamily != AddressFamily.InterNetwork
            || to.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new FormatException($"Диапазон «{text}» не разобран. Ожидается вид 192.168.1.10-192.168.1.40.");
        }

        var first = ToUInt32(from);
        var last = ToUInt32(to);

        if (first > last)
        {
            (first, last) = (last, first);
        }

        Guard(first, last, text);

        return new AddressRange(first, last, text);
    }

    private static AddressRange ParseSingle(string text)
    {
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new FormatException($"Адрес «{text}» не разобран.");
        }

        var value = ToUInt32(address);

        return new AddressRange(value, value, text);
    }

    private static AddressRange FromPrefix(uint address, int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
        {
            throw new FormatException($"Длина префикса {prefixLength} вне диапазона 0…32.");
        }

        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var first = address & mask;
        var last = first | ~mask;

        // Адрес сети и широковещательный не опрашиваются: ответа от них не бывает,
        // а широковещательный запрос — это уже другое действие, и делать его нечаянно
        // при обычном сканировании нельзя.
        if (prefixLength <= 30)
        {
            first++;
            last--;
        }

        var text = $"{FromUInt32(address & mask)}/{prefixLength.ToString(CultureInfo.InvariantCulture)}";
        Guard(first, last, text);

        return new AddressRange(first, last, text);
    }

    private static void Guard(uint first, uint last, string text)
    {
        var count = last - first + 1;

        if (count > MaxHostCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"Диапазон «{text}» охватывает {count.ToString(CultureInfo.InvariantCulture)} адресов "
                + $"при пределе {MaxHostCount.ToString(CultureInfo.InvariantCulture)}. "
                + "Почти наверняка это опечатка в маске — укажите диапазон точнее.");
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    public override string ToString() => Text;
}
