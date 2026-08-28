using System.Net;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Profiles;

namespace StormMachine.Application.Profiles;

/// <summary>
/// Профили сетевого окружения: узнать, где мы, и переключиться.
/// </summary>
/// <remarks>
/// Продукт узнаёт сеть, но <b>не переключает профиль сам</b>. Смена профиля меняет
/// пороги и состав работающих мониторов; сделать это молча значило бы поменять смысл
/// измерений за спиной оператора — и он узнал бы об этом, увидев необъяснимый алерт
/// или необъяснимую тишину.
/// <para>
/// Поэтому здесь два раздельных действия: <see cref="DetectAsync"/> — догадка,
/// <see cref="ActivateAsync"/> — решение человека.
/// </para>
/// </remarks>
public sealed class ProfileService(
    IProfileStore store,
    IMonitorStore monitors,
    INetworkEnvironment environment,
    IArpResolver arp)
{
    private readonly IProfileStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IMonitorStore _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
    private readonly INetworkEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));
    private readonly IArpResolver _arp = arp ?? throw new ArgumentNullException(nameof(arp));

    public Task<IReadOnlyList<NetworkProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        _store.ListAsync(cancellationToken);

    public Task<NetworkProfile?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        _store.GetActiveAsync(cancellationToken);

    public Task<NetworkProfile?> FindAsync(string nameOrId, CancellationToken cancellationToken = default) =>
        _store.FindAsync(nameOrId, cancellationToken);

    /// <summary>
    /// Приметы сети, в которой машина находится сейчас.
    /// </summary>
    /// <remarks>
    /// MAC шлюза берётся через ARP, а не из настроек: настройки говорят, какой адрес
    /// назначен шлюзом, а ARP — какое железо на нём стоит. Переезд в другой офис
    /// с той же адресацией по настройкам неотличим, по MAC — отличим сразу.
    /// </remarks>
    public NetworkSignature CurrentSignature()
    {
        var adapter = _environment.GetPrimaryAdapter();

        if (adapter is null)
        {
            return new NetworkSignature();
        }

        var gateway = adapter.Gateways.Count > 0 ? adapter.Gateways[0] : null;
        string? mac = null;

        if (gateway is not null && IPAddress.TryParse(gateway, out var address))
        {
            try
            {
                mac = _arp.Resolve(address);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.NetworkInformation.NetworkInformationException)
            {
                // Шлюз может не отвечать на ARP — например, за VPN. Это не ошибка,
                // просто самой надёжной приметы не будет.
                mac = null;
            }
        }

        return new NetworkSignature
        {
            GatewayMac = mac,
            GatewayAddress = gateway,
            Subnet = Subnet(adapter),
        };
    }

    /// <summary>Какой профиль похож на текущую сеть. Догадка, а не решение.</summary>
    public async Task<ProfileGuess?> DetectAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        return ProfileMatcher.Guess(profiles, CurrentSignature());
    }

    /// <summary>
    /// Переключает профиль и приводит мониторы в соответствие с ним.
    /// </summary>
    /// <remarks>
    /// Мониторы чужих профилей выключаются, а не удаляются: профиль — это где мы
    /// сейчас, а не что мы навсегда решили. Вернувшись в офис, оператор ждёт свои
    /// офисные проверки на месте, а не заведённых заново.
    /// <para>
    /// Мониторы, не принадлежащие ни одному профилю, не трогаются вовсе: они заведены
    /// вне этой механики, и распоряжаться ими профиль не вправе.
    /// </para>
    /// </remarks>
    public async Task<int> ActivateAsync(Guid? id, CancellationToken cancellationToken = default)
    {
        await _store.ActivateAsync(id, cancellationToken).ConfigureAwait(false);

        var profiles = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var owned = profiles
            .SelectMany(p => p.Monitors.Select(m => (Monitor: m, Profile: p.Id)))
            .ToLookup(x => x.Monitor, x => x.Profile);

        var all = await _monitors.ListAsync(cancellationToken).ConfigureAwait(false);
        var changed = 0;

        foreach (var monitor in all)
        {
            if (!owned.Contains(monitor.Id))
            {
                continue;
            }

            var wanted = id is { } active && owned[monitor.Id].Contains(active);

            if (monitor.IsEnabled == wanted)
            {
                continue;
            }

            await _monitors.SaveAsync(
                monitor with
                {
                    IsEnabled = wanted,
                    UpdatedUtc = DateTimeOffset.UtcNow,

                    // Срок назначается заново: старый, оставшийся с выключения,
                    // дал бы залп пропущенных проверок сразу после включения.
                    NextDueUtc = wanted ? monitor.Schedule.NextAfter(DateTimeOffset.UtcNow) : null,
                },
                cancellationToken).ConfigureAwait(false);

            changed++;
        }

        return changed;
    }

    public Task SaveAsync(NetworkProfile profile, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(profile, cancellationToken);

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(id, cancellationToken);

    /// <summary>Подсеть адаптера в нотации CIDR.</summary>
    private static string? Subnet(NetworkAdapter adapter)
    {
        if (adapter.IPv4Address is not { } text
            || adapter.PrefixLength <= 0
            || !IPAddress.TryParse(text, out var address))
        {
            return null;
        }

        return AddressRange.FromInterface(address, adapter.PrefixLength).Text;
    }
}
