using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Measurements;

namespace StormMachine.Platform;

/// <summary>
/// Сведения о сетевом окружении Windows.
/// </summary>
/// <remarks>
/// Главная задача — не перечислить адаптеры, а <b>распознать их тип</b>. Замеры этапа
/// исследования показали, что через виртуальный коммутатор Hyper-V p99 оказался в 18 раз
/// выше p50, причём источник шума — сам коммутатор. Без распознавания оператор припишет
/// джиттер гипервизора своей сети (docs/02-research.md §3.1).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkEnvironment : INetworkEnvironment
{
    /// <summary>Признаки виртуальных коммутаторов в имени или описании адаптера.</summary>
    private static readonly string[] VirtualMarkers =
    [
        "hyper-v", "vethernet", "vmware", "virtualbox", "vbox", "virtual switch",
        "virtio", "qemu", "parallels", "docker", "wsl", "loopback adapter",
    ];

    /// <summary>Признаки VPN-адаптеров и туннелей.</summary>
    private static readonly string[] VpnMarkers =
    [
        "vpn", "wireguard", "openvpn", "tailscale", "zerotier", "tap-windows",
        "anyconnect", "forticlient", "globalprotect", "nordlynx", "proton",
        "wintun", "softether", "pptp", "l2tp", "sstp", "ikev2",
    ];

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public IReadOnlyList<NetworkAdapter> GetAdapters()
    {
        var adapters = new List<NetworkAdapter>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            var unicast = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            adapters.Add(new NetworkAdapter
            {
                Id = nic.Id,
                Name = nic.Name,
                Description = nic.Description,
                Kind = DetectKind(nic),
                IPv4Address = unicast?.Address.ToString(),
                PrefixLength = unicast?.PrefixLength ?? 0,
                IPv6Addresses = [.. properties.UnicastAddresses
                    .Where(a => IpAddressScope.IsGloballyRoutableV6(a.Address))
                    .Select(a => a.Address.ToString())],
                Gateways = [.. properties.GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address.ToString())],
                DnsServers = [.. properties.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString())],
                MacAddress = FormatMac(nic.GetPhysicalAddress()),
                SpeedBitsPerSecond = nic.Speed > 0 ? nic.Speed : 0,
                IsUp = nic.OperationalStatus == OperationalStatus.Up,
            });
        }

        return adapters;
    }

    public NetworkAdapter? GetPrimaryAdapter()
    {
        // Первичный — тот, через который уходит трафик по умолчанию: поднят, с адресом
        // и с маршрутом по умолчанию. Физические предпочитаются виртуальным: если рядом
        // работает vSwitch, для измерений лучше взять реальный адаптер.
        return GetAdapters()
            .Where(a => a.IsUp && a.IPv4Address is not null && a.Gateways.Count > 0)
            .OrderBy(a => a.Kind switch
            {
                AdapterKind.Physical => 0,
                AdapterKind.Wireless => 1,
                AdapterKind.Virtual => 2,
                AdapterKind.Vpn => 3,
                AdapterKind.Tunnel => 4,
                _ => 5,
            })
            .ThenByDescending(a => a.SpeedBitsPerSecond)
            .FirstOrDefault();
    }

    /// <summary>
    /// Определяет тип адаптера. Порядок проверок важен: VPN часто представляется
    /// системе как обычный Ethernet, поэтому имя и описание разбираются до типа интерфейса.
    /// </summary>
    internal static AdapterKind DetectKind(NetworkInterface nic)
    {
        ArgumentNullException.ThrowIfNull(nic);

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return AdapterKind.Loopback;
        }

        var haystack = $"{nic.Name} {nic.Description}".ToLowerInvariant();

        if (ContainsAny(haystack, VpnMarkers))
        {
            return AdapterKind.Vpn;
        }

        if (ContainsAny(haystack, VirtualMarkers))
        {
            return AdapterKind.Virtual;
        }

        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => AdapterKind.Wireless,
            NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => AdapterKind.Tunnel,
            NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx => AdapterKind.Physical,
            _ => AdapterKind.Unknown,
        };
    }

    private static bool ContainsAny(string haystack, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (haystack.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join(':', bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
