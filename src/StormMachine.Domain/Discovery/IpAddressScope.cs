using System.Net;
using System.Net.Sockets;

namespace StormMachine.Domain.Discovery;

/// <summary>Область действия адреса IPv6 — насколько далеко он виден.</summary>
public enum Ipv6Scope
{
    /// <summary>Не адрес IPv6.</summary>
    NotIpv6,

    /// <summary>Петля <c>::1</c> или неопределённый <c>::</c>: не виден никому.</summary>
    Loopback,

    /// <summary>Локальный для канала <c>fe80::/10</c>: виден в пределах сегмента.</summary>
    LinkLocal,

    /// <summary>Уникальный локальный <c>fc00::/7</c>: виден внутри организации.</summary>
    UniqueLocal,

    /// <summary>Групповой <c>ff00::/8</c>: адрес назначения, а не отправителя.</summary>
    Multicast,

    /// <summary>Туннель поверх IPv4: Teredo <c>2001:0::/32</c> или 6to4 <c>2002::/16</c>.</summary>
    Tunnelled,

    /// <summary>Глобальный: машина видна снаружи этим адресом.</summary>
    Global,
}

/// <summary>
/// Область действия адреса.
/// </summary>
/// <remarks>
/// Правило вынесено сюда, потому что ошибка в нём — это ложь оператору, а не неудобство.
/// Первая версия проверки IPv6 считала глобальным всё, что не локально для канала, — и
/// объявляла машину готовой к IPv6 на основании адреса петли <c>::1</c>, который есть
/// на любой машине всегда. Инструмент сообщал «адрес есть, связности нет», хотя честный
/// ответ был «адреса нет».
/// <para>
/// Туннели названы отдельно от глобальных сознательно. Teredo и 6to4 дают настоящую
/// связность по IPv6, но поверх IPv4 и через чужой ретранслятор: задержка у них своя,
/// и «готовность к IPv6» на туннеле — это другой ответ, чем на нативном адресе.
/// </para>
/// </remarks>
public static class IpAddressScope
{
    public static Ipv6Scope ClassifyV6(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return Ipv6Scope.NotIpv6;
        }

        if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            return Ipv6Scope.Loopback;
        }

        if (address.IsIPv6Multicast)
        {
            return Ipv6Scope.Multicast;
        }

        if (address.IsIPv6LinkLocal)
        {
            return Ipv6Scope.LinkLocal;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out var written) || written != 16)
        {
            return Ipv6Scope.NotIpv6;
        }

        // Устаревший site-local fec0::/10 (RFC 3879) — отозван, но встречается
        // в старых конфигурациях, и глобальным он не был никогда.
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0)
        {
            return Ipv6Scope.UniqueLocal;
        }

        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return Ipv6Scope.UniqueLocal;
        }

        if (address.IsIPv6Teredo || (bytes[0] == 0x20 && bytes[1] == 0x02))
        {
            return Ipv6Scope.Tunnelled;
        }

        // Отображённый адрес IPv4 (::ffff:0:0/96) — это адрес IPv4 в записи IPv6,
        // и связности по IPv6 он не означает.
        if (address.IsIPv4MappedToIPv6)
        {
            return Ipv6Scope.NotIpv6;
        }

        return Ipv6Scope.Global;
    }

    /// <summary>Адрес, которым машина видна снаружи по IPv6.</summary>
    public static bool IsGloballyRoutableV6(IPAddress? address) =>
        ClassifyV6(address) == Ipv6Scope.Global;

    public static string Describe(Ipv6Scope scope) => scope switch
    {
        Ipv6Scope.Loopback => "петля — не виден никому",
        Ipv6Scope.LinkLocal => "локальный для канала — виден только в своём сегменте",
        Ipv6Scope.UniqueLocal => "уникальный локальный — виден внутри организации, но не в интернете",
        Ipv6Scope.Multicast => "групповой — адрес назначения, а не отправителя",
        Ipv6Scope.Tunnelled => "туннель поверх IPv4 — связность есть, но через чужой ретранслятор",
        Ipv6Scope.Global => "глобальный",
        _ => "не адрес IPv6",
    };
}
