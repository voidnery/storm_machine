using StormMachine.Application.Abstractions;
using StormMachine.Domain.Capture;

namespace StormMachine.Application.Capture;

/// <summary>
/// Пассивное прослушивание сети.
/// </summary>
/// <remarks>
/// Уровень 2. Всё, что он добавляет, — это <b>слушать своим адаптером</b>: соседей,
/// которые объявляются сами, и ответы DHCP, которые всё равно широковещательны.
/// Ни одного кадра в сеть не отправляется.
/// <para>
/// Драйвер захвата продукт не распространяет ни при каких условиях — лицензия NPSL
/// это запрещает. Поэтому уровень необязателен целиком: без драйвера всё остальное
/// работает как работало, а здесь честно сказано, чего не хватает и откуда это взять.
/// </para>
/// </remarks>
public sealed class CaptureService(ICaptureProvider capture, INetworkEnvironment environment)
{
    /// <summary>Откуда берут драйвер. Продукт его не распространяет.</summary>
    public const string DriverSite = "https://npcap.com";

    private readonly ICaptureProvider _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly INetworkEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));

    public CaptureRefusal Availability => _capture.Availability;

    public bool IsAvailable => _capture.Availability == CaptureRefusal.None;

    public string? DriverDescription => _capture.DriverDescription;

    /// <summary>Почему нельзя и что с этим делать.</summary>
    public string Explain() => _capture.Availability switch
    {
        CaptureRefusal.None => "Захват доступен.",
        CaptureRefusal.NeedsElevation =>
            "Драйвер захвата установлен, но не пускает. Npcap умеет ставиться с ограничением "
            + "доступа администраторами — перезапустите продукт от имени администратора "
            + "либо переустановите драйвер без этого ограничения.",
        CaptureRefusal.NoAdapters =>
            "Драйвер захвата есть, но подходящих адаптеров он не показывает.",
        _ =>
            "Драйвер захвата не установлен. Npcap ставится отдельно и вручную: продукт "
            + $"его не распространяет ни при каких условиях — лицензия NPSL это запрещает. {DriverSite}",
    };

    /// <summary>
    /// Адаптеры, дополненные именами из системы.
    /// </summary>
    /// <remarks>
    /// Сопоставление идёт по MAC, а не по имени: драйвер захвата называет устройства
    /// своими именами вида <c>\Device\NPF_{GUID}</c>, и с тем, что показывает система,
    /// они не совпадают ни на одной версии Windows. Оператору нужно второе.
    /// </remarks>
    public IReadOnlyList<CaptureAdapter> Adapters()
    {
        var system = _environment.GetAdapters()
            .Where(a => a.MacAddress is not null)
            .ToDictionary(a => Normalize(a.MacAddress!), a => a.Name, StringComparer.OrdinalIgnoreCase);

        return
        [
            .. _capture.Adapters().Select(a => a.MacAddress is { } mac
                                               && system.TryGetValue(Normalize(mac), out var name)
                ? a with { SystemName = name }
                : a),
        ];
    }

    /// <summary>
    /// Адаптер, на котором разумно слушать по умолчанию.
    /// </summary>
    /// <remarks>
    /// Тот, через который идёт маршрут по умолчанию: он смотрит в ту сеть, про которую
    /// спрашивают. Выбирать первый попавшийся нельзя — на машине с Hyper-V первым
    /// оказывается виртуальный коммутатор, и прослушивание уходит не в ту сеть.
    /// </remarks>
    public CaptureAdapter? Primary()
    {
        var adapters = Adapters();

        if (adapters.Count == 0)
        {
            return null;
        }

        if (_environment.GetPrimaryAdapter() is { MacAddress: { } mac })
        {
            var match = adapters.FirstOrDefault(a =>
                a.MacAddress is not null
                && string.Equals(Normalize(a.MacAddress), Normalize(mac), StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return adapters.FirstOrDefault(a => !a.IsLoopback) ?? adapters[0];
    }

    public Task<CaptureResult> ListenAsync(
        CaptureAdapter adapter,
        CaptureOptions options,
        CancellationToken cancellationToken = default) =>
        _capture.ListenAsync(adapter, options, cancellationToken);

    /// <summary>
    /// Шлюзы, известные системе, — чтобы отличить объявленный DHCP-сервером шлюз от нашего.
    /// </summary>
    /// <remarks>
    /// Единственное утверждение о постороннем DHCP, которое продукт берётся делать сам:
    /// сервер объявляет шлюз, которого мы не знаем. Всё прочее — факты без вердикта,
    /// потому что две законные пары DHCP в одном домене встречаются не реже подставного
    /// сервера, и различить их может только тот, кто знает свою сеть.
    /// </remarks>
    public IReadOnlyList<string> KnownGateways() =>
    [
        .. _environment.GetAdapters()
            .SelectMany(a => a.Gateways)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private static string Normalize(string mac) => mac.Replace(":", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal);
}
