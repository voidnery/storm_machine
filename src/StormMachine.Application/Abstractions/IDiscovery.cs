using System.Net;
using StormMachine.Domain.Discovery;

namespace StormMachine.Application.Abstractions;

/// <summary>
/// Разрешение адреса в MAC средствами операционной системы.
/// </summary>
/// <remarks>
/// Работает <b>без прав администратора и без драйвера захвата</b> — это и есть
/// то, что делает инвентарь уровня 0 полезным (<c>R-03</c>). Через <c>SendARP</c>
/// и таблицу ARP: первое активно спрашивает, второе читает уже известное.
/// </remarks>
public interface IArpResolver
{
    /// <summary>Спрашивает MAC у самого узла. <c>null</c> — узел не в той же сети или не ответил.</summary>
    string? Resolve(IPAddress address);

    /// <summary>Читает системную таблицу ARP: адрес → MAC.</summary>
    IReadOnlyDictionary<string, string> ReadTable();
}

/// <summary>Вендор по префиксу MAC из реестра IEEE.</summary>
/// <remarks>
/// База встроена в поставку: вендор по MAC входит в уровень 0, и требовать ради него
/// ручных действий значило бы сломать сценарий первого запуска. Реестр IEEE публичный,
/// в отличие от Npcap и DB-IP, которые в поставку не входят и входить не могут.
/// </remarks>
public interface IOuiCatalog
{
    /// <summary>Сколько записей загружено.</summary>
    int Count { get; }

    /// <summary>Вендор по MAC в любом обычном написании. <c>null</c> — префикс не найден.</summary>
    string? Lookup(string macAddress);
}

/// <summary>Обнаружение узлов в сети.</summary>
public interface IDiscoveryService
{
    /// <summary>
    /// Сканирует диапазон. Устройства отдаются по мере обнаружения, а не в конце:
    /// сканирование /24 идёт секунды, но оператор должен видеть движение сразу.
    /// </summary>
    Task<DiscoveryScan> ScanAsync(
        DiscoveryRequest request,
        Action<DiscoveryProgress>? onProgress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Что и как сканировать.</summary>
public sealed record DiscoveryRequest
{
    public required AddressRange Range { get; init; }

    /// <summary>
    /// Сколько адресов опрашивать одновременно.
    /// </summary>
    /// <remarks>
    /// Ограничение темпа — не оптимизация, а обязанность: продукт сканирует чужую сеть.
    /// Значение по умолчанию подобрано так, чтобы /24 укладывался в секунды,
    /// не создавая всплеска, который система обнаружения вторжений примет за разведку.
    /// </remarks>
    public int Parallelism { get; init; } = 64;

    /// <summary>Сколько ждать ответа от одного адреса.</summary>
    public int TimeoutMs { get; init; } = 700;

    /// <summary>Узнавать имена узлов: обратный DNS и NetBIOS.</summary>
    public bool ResolveNames { get; init; } = true;

    /// <summary>
    /// Проверять несколько частых портов у узлов, промолчавших на ICMP.
    /// </summary>
    /// <remarks>
    /// Хост с включённым брандмауэром Windows не отвечает на ICMP, но принимает
    /// соединения — без этой проверки половина рабочих станций в офисе оказалась бы
    /// «недоступной».
    /// </remarks>
    public bool ProbeCommonPorts { get; init; } = true;
}

/// <summary>Хранилище инвентаря: сканирования, устройства, журнал активных действий.</summary>
public interface IDeviceStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveScanAsync(DiscoveryScan scan, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscoveryScan>> ListScansAsync(int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>Сканирование со всеми устройствами. <c>null</c> — не найдено.</summary>
    Task<DiscoveryScan?> GetScanAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сводный инвентарь: все устройства, когда-либо виденные, со свидетельствами.
    /// </summary>
    Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Добавляет свидетельство от оператора — оно перекрывает наблюдения.</summary>
    Task PinAsync(string identity, Evidence evidence, CancellationToken cancellationToken = default);

    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit = 50, CancellationToken cancellationToken = default);
}
