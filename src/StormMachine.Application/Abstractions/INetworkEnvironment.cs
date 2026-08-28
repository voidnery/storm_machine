using StormMachine.Domain.Measurements;

namespace StormMachine.Application.Abstractions;

/// <summary>Сетевой адаптер машины, на которой идёт измерение.</summary>
public sealed record NetworkAdapter
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required AdapterKind Kind { get; init; }

    public string? IPv4Address { get; init; }

    public int PrefixLength { get; init; }

    /// <summary>
    /// Глобальные адреса IPv6 — те, с которых машина видна снаружи.
    /// </summary>
    /// <remarks>
    /// Только глобальные: локальные для канала (<c>fe80::/10</c>) есть у любого адаптера
    /// всегда, и наличие такого адреса не означает, что IPv6 работает. Считать их
    /// признаком готовности значило бы объявить готовым любой Windows из коробки.
    /// </remarks>
    public IReadOnlyList<string> IPv6Addresses { get; init; } = [];

    public IReadOnlyList<string> Gateways { get; init; } = [];

    public IReadOnlyList<string> DnsServers { get; init; } = [];

    public string? MacAddress { get; init; }

    /// <summary>Скорость линка в битах в секунду; 0 — неизвестна.</summary>
    public long SpeedBitsPerSecond { get; init; }

    public bool IsUp { get; init; }

    /// <summary>Подсеть в нотации CIDR, если адрес и длина префикса известны.</summary>
    public string? SubnetCidr => IPv4Address is null || PrefixLength <= 0
        ? null
        : $"{IPv4Address}/{PrefixLength}";
}

/// <summary>
/// Сведения о сетевом окружении. Порт: реализация живёт в слое платформы.
/// </summary>
/// <remarks>
/// Определение типа адаптера — не украшение. Через виртуальный коммутатор или VPN
/// измерения содержат собственный шум, и оператор обязан это видеть до того, как
/// поверит цифрам (docs/02-research.md §3.1).
/// </remarks>
public interface INetworkEnvironment
{
    IReadOnlyList<NetworkAdapter> GetAdapters();

    /// <summary>Адаптер с маршрутом по умолчанию, если он есть.</summary>
    NetworkAdapter? GetPrimaryAdapter();

    /// <summary>Работает ли процесс с правами администратора.</summary>
    bool IsElevated { get; }
}
