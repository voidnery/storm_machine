using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using StormMachine.Application.Abstractions;
using StormMachine.Platform.Geo;

namespace StormMachine.Platform;

/// <summary>
/// Обогащение узлов маршрута именами и данными об автономных системах.
/// </summary>
/// <remarks>
/// Обратный DNS работает всегда — он есть в системе. Данные ASN требуют офлайн-базы
/// в формате MMDB; без неё остаются адреса и имена, и трассировка по-прежнему полезна.
/// <para>
/// База не входит в поставку: <b>DB-IP Lite</b> распространяется по CC BY-SA 4.0
/// и требует указания источника. Оператор скачивает её сам, а продукт честно сообщает,
/// чего без неё нет — та же градация по зависимостям, что и для SNMP с захватом пакетов.
/// </para>
/// </remarks>
public sealed class HopAnnotator : IHopAnnotator
{
    /// <summary>Сколько имён разрешаем одновременно.</summary>
    private const int MaxParallelLookups = 12;

    /// <summary>Предел ожидания обратного DNS: узел мог и не иметь записи.</summary>
    private static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, HopAnnotation> _cache = new(StringComparer.Ordinal);
    private readonly IAsnDatabase? _asn;

    public HopAnnotator(IAsnDatabase? asn = null)
    {
        _asn = asn;
        AsnDatabaseHint = asn?.Location ?? AsnDatabase.DefaultPath();
    }

    public bool HasAsnData => _asn?.IsAvailable == true;

    public string AsnDatabaseHint { get; }

    public string? Attribution => HasAsnData ? AsnDatabase.Attribution : null;

    public async Task<IReadOnlyDictionary<string, HopAnnotation>> AnnotateAsync(
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var unique = addresses
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, HopAnnotation>(StringComparer.Ordinal);
        var pending = new List<string>();

        foreach (var address in unique)
        {
            if (_cache.TryGetValue(address, out var cached))
            {
                result[address] = cached;
                continue;
            }

            pending.Add(address);
        }

        if (pending.Count == 0)
        {
            return result;
        }

        using var gate = new SemaphoreSlim(MaxParallelLookups);

        var tasks = pending.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var annotation = await AnnotateOneAsync(address, cancellationToken).ConfigureAwait(false);
                _cache[address] = annotation;
                return annotation;
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var annotation in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            result[annotation.Address] = annotation;
        }

        return result;
    }

    private async Task<HopAnnotation> AnnotateOneAsync(string address, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out var parsed))
        {
            return new HopAnnotation { Address = address };
        }

        if (IsPrivate(parsed))
        {
            // Свою сеть обогащать нечем: обратной зоны обычно нет, автономной системы
            // не существует. Ходить за этим в сеть — впустую тратить время трассировки.
            return new HopAnnotation { Address = address, IsPrivate = true };
        }

        var hostName = await ResolveHostNameAsync(parsed, cancellationToken).ConfigureAwait(false);
        var asn = _asn?.Lookup(parsed);

        return new HopAnnotation
        {
            Address = address,
            HostName = hostName,
            AsNumber = asn?.Number,
            AsOrganization = asn?.Organization,
            Country = asn?.Country,
        };
    }

    private static async Task<string?> ResolveHostNameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            // Перегрузки с токеном для IPAddress в System.Net.Dns нет, поэтому предел
            // ожидания навешивается снаружи. Запрос при этом продолжает жить в фоне —
            // и это правильно: его результат попадёт в кэш резолвера.
            var entry = await Dns.GetHostEntryAsync(address)
                .WaitAsync(ReverseDnsTimeout, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Отсутствие обратной записи — норма, а не ошибка.
            return null;
        }
    }

    /// <summary>Диапазоны из RFC 1918, RFC 6598 и localhost.</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            127 => true,
            169 when octets[1] == 254 => true,
            172 when octets[1] is >= 16 and <= 31 => true,
            192 when octets[1] == 168 => true,
            100 when octets[1] is >= 64 and <= 127 => true,
            _ => false,
        };
    }
}
