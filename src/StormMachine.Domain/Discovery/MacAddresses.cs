using System.Globalization;

namespace StormMachine.Domain.Discovery;

/// <summary>
/// Что можно сказать об устройстве по самому его MAC-адресу.
/// </summary>
/// <remarks>
/// Реестр IEEE отвечает на вопрос «кто выпустил», но у части адресов производителя нет
/// вовсе — и назвать такой адрес именем из реестра значит сказать формально верное
/// и совершенно бесполезное.
/// <para>
/// Самый заметный случай — виртуальные адреса протоколов резервирования шлюза. Адрес
/// вида <c>00-00-5E-00-01-C8</c> реестр относит к IANA, и в таблице появляется строка
/// «ICANN, IANA Department». На деле это адрес VRRP, а последний байт — номер группы
/// резервирования: то есть шлюз не отдельная железка, а резервируемая пара. Ровно этот
/// факт и ищут, когда смотрят на шлюз.
/// </para>
/// </remarks>
public static class MacAddresses
{
    /// <summary>Длина MAC в шестнадцатеричных знаках.</summary>
    private const int Length = 12;

    /// <summary>
    /// Адрес назначен локально, а не выдан производителю.
    /// </summary>
    /// <remarks>
    /// Второй бит первого октета означает локальное назначение. Так выглядят случайные
    /// адреса телефонов с приватным Wi-Fi, виртуальные адаптеры Hyper-V и Docker, мосты
    /// и агрегированные интерфейсы. Вендора у такого адреса нет и быть не может.
    /// </remarks>
    public static bool IsLocallyAdministered(string? macAddress)
    {
        var digits = Normalize(macAddress);

        return digits.Length >= 2
               && int.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first)
               && (first & 0x02) != 0;
    }

    /// <summary>
    /// Короткая подпись виртуального адреса протокола резервирования.
    /// <c>null</c> — обычный адрес.
    /// </summary>
    /// <remarks>
    /// Три протокола покрывают почти всё, что встречается: VRRP (стандарт, RFC 5798),
    /// HSRP и GLBP (Cisco). У всех троих последние биты адреса несут номер группы,
    /// и он полезен: по нему видно, одна на сети группа резервирования или несколько.
    /// <para>
    /// Подписи короткие намеренно: они живут в столбце таблицы рядом с именами вендоров.
    /// Длинная формулировка обрезалась бы ровно на номере группы — на единственном,
    /// что в ней несёт сведения. Что такое эти адреса, объясняется один раз под таблицей.
    /// </para>
    /// </remarks>
    public static string? DescribeVirtual(string? macAddress)
    {
        var digits = Normalize(macAddress);

        if (digits.Length < Length)
        {
            return null;
        }

        // VRRP: 00-00-5E-00-01-XX для IPv4 и 00-00-5E-00-02-XX для IPv6.
        if (digits.StartsWith("00005E0001", StringComparison.Ordinal))
        {
            return $"VRRP, группа {Group(digits, 10, 2)}";
        }

        if (digits.StartsWith("00005E0002", StringComparison.Ordinal))
        {
            return $"VRRP для IPv6, группа {Group(digits, 10, 2)}";
        }

        // HSRP первой версии: 00-00-0C-07-AC-XX.
        if (digits.StartsWith("00000C07AC", StringComparison.Ordinal))
        {
            return $"HSRP (Cisco), группа {Group(digits, 10, 2)}";
        }

        // HSRP второй версии: 00-00-0C-9F-FX-XX, номер группы занимает двенадцать бит.
        if (digits.StartsWith("00000C9FF", StringComparison.Ordinal))
        {
            return $"HSRP v2 (Cisco), группа {Group(digits, 9, 3)}";
        }

        // GLBP: 00-07-B4-00-XX-YY, где XX — номер группы, YY — номер пересылающего.
        // Второй полезен: в группе несколько пересылающих, и по нему видно, какой
        // из них отвечает за конкретного клиента.
        if (digits.StartsWith("0007B400", StringComparison.Ordinal))
        {
            return $"GLBP (Cisco), группа {Group(digits, 8, 2)}.{Group(digits, 10, 2)}";
        }

        return null;
    }

    /// <summary>
    /// Как показать принадлежность адреса, когда вендор из реестра ничего не объясняет.
    /// </summary>
    /// <remarks>
    /// Порядок ответов важен. Виртуальный адрес сильнее вендора: у VRRP реестр называет
    /// IANA, и это правда, но говорит она не о том. Локальный адрес показывается вместо
    /// прочерка: прочерк читался бы как «не нашли в базе», хотя искать там нечего.
    /// </remarks>
    public static string Describe(string? macAddress, string? vendor)
    {
        if (DescribeVirtual(macAddress) is { } virtualAddress)
        {
            return virtualAddress;
        }

        if (vendor is { Length: > 0 })
        {
            return vendor;
        }

        return IsLocallyAdministered(macAddress) ? "локальный MAC" : "—";
    }

    /// <summary>Что означают такие адреса — строка для пояснения под таблицей.</summary>
    public const string VirtualExplanation =
        "VRRP, HSRP и GLBP — протоколы резервирования шлюза: такой адрес принадлежит "
        + "не отдельной железке, а группе устройств, подменяющих друг друга. "
        + "Номер группы виден в самом адресе.";

    private static string Group(string digits, int start, int length) =>
        int.TryParse(digits.AsSpan(start, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : "?";

    /// <summary>Оставляет только шестнадцатеричные знаки: написаний у MAC много.</summary>
    private static string Normalize(string? macAddress)
    {
        if (string.IsNullOrEmpty(macAddress))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Length];
        var length = 0;

        foreach (var c in macAddress)
        {
            if (length == buffer.Length)
            {
                break;
            }

            if (char.IsAsciiHexDigit(c))
            {
                buffer[length++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }
}
