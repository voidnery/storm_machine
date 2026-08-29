using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;

namespace StormMachine.Application.Abstractions;

/// <summary>Точка ряда загрузки порта: то, что легло в историю.</summary>
/// <remarks>
/// Хранится посчитанная разница двух снимков, а не сырые счётчики. Сырой счётчик
/// 32 бит переполняется на гигабитном порту примерно за полминуты, и ряд таких
/// значений без пометок о переполнении бесполезен; разница считается в момент опроса,
/// когда оба снимка на руках и известен промежуток между ними.
/// </remarks>
public sealed record PortLoadPoint
{
    /// <summary>Адрес или имя устройства, у которого спрашивали.</summary>
    public required string Device { get; init; }

    public required int IfIndex { get; init; }

    public string? IfName { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    public required TimeSpan Interval { get; init; }

    public required double InBitsPerSecond { get; init; }

    public required double OutBitsPerSecond { get; init; }

    /// <summary>Скорость порта. 0 — неизвестна, и тогда проценты не считаются.</summary>
    public long SpeedBitsPerSecond { get; init; }

    public long InErrors { get; init; }

    public long OutErrors { get; init; }

    public long InDiscards { get; init; }

    public long OutDiscards { get; init; }

    /// <summary>Загрузка входящего направления в процентах. <c>null</c> — скорость неизвестна.</summary>
    public double? InPercent => SpeedBitsPerSecond > 0 ? InBitsPerSecond / SpeedBitsPerSecond * 100 : null;

    public double? OutPercent => SpeedBitsPerSecond > 0 ? OutBitsPerSecond / SpeedBitsPerSecond * 100 : null;

    /// <summary>Ошибок и отбросов суммарно — то, по чему судят о состоянии кабеля.</summary>
    public long Faults => InErrors + OutErrors + InDiscards + OutDiscards;
}

/// <summary>Сосед, услышанный в эфире, с историей наблюдений.</summary>
public sealed record HeardNeighbor
{
    /// <summary>Наш адаптер, на котором он услышан.</summary>
    public required string LocalInterface { get; init; }

    public required string ChassisId { get; init; }

    public required string PortId { get; init; }

    public string? SystemName { get; init; }

    public string? PortName { get; init; }

    public required NeighborProtocol Protocol { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }
}

/// <summary>Сервер DHCP, услышанный в сегменте, с историей наблюдений.</summary>
/// <remarks>
/// Ключ включает объявляемый шлюз намеренно. Сервер, начавший объявлять другой шлюз, —
/// это событие, ради которого захват и слушают; обновить строку на месте значило бы
/// его потерять.
/// </remarks>
public sealed record HeardDhcpServer
{
    public required string ServerAddress { get; init; }

    public required string OfferedGateway { get; init; }

    public string? ServerMac { get; init; }

    public IReadOnlyList<string> OfferedDns { get; init; } = [];

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Сколько раз услышан.</summary>
    public int Sightings { get; init; }
}

/// <summary>
/// История наблюдений за оборудованием.
/// </summary>
/// <remarks>
/// Появилась в И-21 и закрывает два одинаковых долга — И-17 и И-18. Оба вида данных
/// продукт читать умел, а хранить не умел: загрузка порта мерилась на месте
/// и показывалась, услышанные соседи и серверы DHCP показывались и забывались.
/// <para>
/// Без истории продукт отвечает только на «что сейчас», а спрашивают у него другое:
/// «что было с портом ночью» и «когда появился этот сервер DHCP». Второй вопрос
/// особенно: посторонний сервер сам по себе не доказательство — два сервера в одном
/// домене бывают и законно, — а вот появившийся вчера сервер это уже событие.
/// </para>
/// </remarks>
public interface IObservationStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Дописывает точки ряда загрузки портов.</summary>
    Task SavePortLoadAsync(
        IReadOnlyList<PortLoadPoint> points,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ряд загрузки порта за период.
    /// </summary>
    /// <param name="device">Устройство. Пусто — все.</param>
    /// <param name="ifIndex">Порт. <c>null</c> — все порты устройства.</param>
    /// <param name="since">С какого момента.</param>
    Task<IReadOnlyList<PortLoadPoint>> ListPortLoadAsync(
        string? device,
        int? ifIndex,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Запоминает услышанное прослушиванием.
    /// </summary>
    /// <remarks>
    /// Одно и то же соседство, услышанное десять раз, остаётся одним соседством
    /// с обновлённым временем: время первого наблюдения при этом не трогается —
    /// оно и есть ответ на «когда появился».
    /// </remarks>
    Task SaveCaptureAsync(CaptureResult result, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HeardNeighbor>> ListNeighborsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HeardDhcpServer>> ListDhcpAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет наблюдения старше горизонта.
    /// </summary>
    /// <remarks>
    /// Ряды растут линейно и подпадают под ту же политику хранения, что и сэмплы
    /// измерений: без ограничения они превратили бы файл базы в проблему ровно тем же
    /// способом, от которого политика и защищает.
    /// </remarks>
    Task<int> ApplyRetentionAsync(TimeSpan horizon, CancellationToken cancellationToken = default);
}
