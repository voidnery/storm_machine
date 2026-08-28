using System.Net;
using System.Net.Sockets;

namespace StormMachine.Domain.Discovery;

/// <summary>
/// Порядок адресов IPv4 — числовой, а не строковый.
/// </summary>
/// <remarks>
/// Строковое сравнение ставит <c>192.168.200.254</c> раньше <c>192.168.200.3</c>,
/// потому что знак «2» меньше знака «3». Для сортировки списка это уже плохо,
/// а для выбора основного адреса устройства — прямая ошибка: у узла с адресами
/// <c>.3</c> и <c>.254</c> главным оказывался тот, что человек назвал бы последним.
/// <para>
/// Правило вынесено в одно место намеренно. Оно применяется в четырёх: список
/// устройств, различия между сканами, инвентарь и карта. Разойдись они — один и тот же
/// узел показывался бы в разных местах под разными адресами.
/// </para>
/// </remarks>
public static class IpAddressOrder
{
    /// <summary>Адрес как число. Неразобранное и IPv6 уходят в конец.</summary>
    public static uint Of(string? address)
    {
        if (address is null
            || !IPAddress.TryParse(address, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return uint.MaxValue;
        }

        var bytes = parsed.GetAddressBytes();

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>Наименьший адрес из набора — он и становится основным адресом устройства.</summary>
    public static string? Lowest(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        string? best = null;
        var bestOrder = uint.MaxValue;
        var first = true;

        foreach (var address in addresses)
        {
            var order = Of(address);

            // Первый пришедший берётся и тогда, когда разобрать его не удалось:
            // иначе устройство осталось бы вовсе без основного адреса.
            if (first || order < bestOrder
                || (order == bestOrder && string.CompareOrdinal(address, best) < 0))
            {
                best = address;
                bestOrder = order;
                first = false;
            }
        }

        return best;
    }
}
