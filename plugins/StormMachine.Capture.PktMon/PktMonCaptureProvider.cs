using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Adapters;
using StormMachine.Capture.Frames;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;

namespace StormMachine.Capture.PktMon;

/// <summary>
/// Захват встроенным в Windows монитором пакетов.
/// </summary>
/// <remarks>
/// Ответ на то, из-за чего уровень 2 был мёртв у большинства: Npcap продукт
/// не распространяет — лицензия NPSL это запрещает, — а ставить драйвер руками
/// согласится не каждый. <c>pktmon</c> входит в Windows с версии 10 1809, подписан
/// Microsoft и уже стоит на машине. Ставить нечего, лицензия ничего не запрещает.
/// <para>
/// <b>Та же граница, что у Npcap.</b> Фильтры отбирают три вида кадров: LLDP, CDP
/// и ответы DHCP. Неразборчивого режима нет, чужая переписка не собирается,
/// в сеть не уходит ни одного кадра — продукт по-прежнему только слушает.
/// </para>
/// <para>
/// <b>Чем отличается от Npcap.</b> Потока кадров pktmon не даёт: он пишет журнал,
/// который потом превращается в pcapng и читается целиком. Значит, прослушивание
/// отвечает по окончании, а не по ходу, и требует прав администратора всегда,
/// а не иногда. Взамен оно работает без единой установки — спайк-10.
/// </para>
/// </remarks>
public sealed class PktMonCaptureProvider(
    INetworkEnvironment environment,
    ISystemCapabilities system,
    PktMonTool? tool = null) : ICaptureProvider
{
    /// <summary>Кадры соседства: LLDP опознаётся по типу, CDP — по адресу назначения.</summary>
    private const string LldpType = "0x88CC";

    private const string CdpDestination = "01-00-0C-CC-CC-CC";

    /// <summary>Порт сервера DHCP: ответы идут с него.</summary>
    private const int DhcpServerPort = 67;

    /// <summary>
    /// Окно, в котором одинаковые байты считаются одним кадром.
    /// </summary>
    /// <remarks>
    /// pktmon регистрирует пакет в каждой точке стека, через которую тот прошёл,
    /// и на хосте с Hyper-V один кадр приходит три-четыре раза (спайк-10: 21 дубль
    /// из 25 кадров). Два разных объявления одного коммутатора разделены десятками
    /// секунд, так что двухсекундное окно не склеит ничего настоящего.
    /// </remarks>
    private static readonly TimeSpan SameFrame = TimeSpan.FromSeconds(2);

    private readonly INetworkEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));

    private readonly ISystemCapabilities _system = system ?? throw new ArgumentNullException(nameof(system));

    private readonly PktMonTool _tool = tool ?? new PktMonTool();

    public CaptureEngine Engine => CaptureEngine.WindowsBuiltIn;

    public CaptureRefusal Availability
    {
        get
        {
            if (!_tool.Exists)
            {
                return CaptureRefusal.NoDriver;
            }

            // Права спрашиваются до запуска процесса: экран возможностей открывают
            // часто, и дёргать ради него драйвер — плохая плата за ту же правду.
            if (!_system.IsElevated)
            {
                return CaptureRefusal.NeedsElevation;
            }

            return Adapters().Count == 0 ? CaptureRefusal.NoAdapters : CaptureRefusal.None;
        }
    }

    public string? DriverDescription => _tool.Exists
        ? $"встроенный в Windows pktmon {_tool.Version ?? "без версии"}"
        : null;

    /// <summary>
    /// Адаптеры системы, а не компоненты pktmon.
    /// </summary>
    /// <remarks>
    /// Компонентов у одной карты бывает несколько, и показывать оператору стек
    /// Hyper-V вместо карты незачем: выбирает он карту, а какие точки за ней
    /// слушать — дело продукта. Список берётся из системы и поэтому доступен
    /// без прав администратора: оператор видит, что выбирать, ещё до перезапуска.
    /// </remarks>
    public IReadOnlyList<CaptureAdapter> Adapters() =>
    [
        .. _environment.GetAdapters()
            .Where(a => a.IsUp && !string.IsNullOrWhiteSpace(a.MacAddress))

            // Один MAC — одна строка выбора. За ним стоят и карта, и виртуальный
            // коммутатор поверх неё, и служебный фильтр NDIS, который появляется,
            // пока загружен наш же драйвер захвата. Слушаем мы всё равно все точки
            // с этим MAC, а показывать оператору три одинаковые строки незачем.
            .GroupBy(a => PktMonComponents.Normalize(a.MacAddress!), StringComparer.Ordinal)
            .Select(group => AdapterRank.MostRealFirst(group).First())
            .Select(a => new CaptureAdapter
            {
                Id = PktMonComponents.Normalize(a.MacAddress!),
                Description = string.IsNullOrWhiteSpace(a.Description) ? a.Name : a.Description,
                MacAddress = a.MacAddress,
                SystemName = a.Name,
                IsLoopback = a.Kind == Domain.Measurements.AdapterKind.Loopback,
            }),
    ];

    public async Task<CaptureResult> ListenAsync(
        CaptureAdapter adapter,
        CaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);

        var startedUtc = DateTimeOffset.UtcNow;

        if (!options.Neighbors && !options.Dhcp)
        {
            // Слушать не просили ничего — не поднимаем драйвер ради пустого файла.
            return Empty(adapter, startedUtc);
        }

        var work = Directory.CreateTempSubdirectory("storm-захват-");

        try
        {
            var frames = await HearAsync(adapter, options, work.FullName, cancellationToken)
                .ConfigureAwait(false);

            return Understand(adapter, options, frames, startedUtc);
        }
        finally
        {
            await TidyAsync().ConfigureAwait(false);

            Forget(work);
        }
    }

    private async Task<IReadOnlyList<CapturedFrame>> HearAsync(
        CaptureAdapter adapter,
        CaptureOptions options,
        string work,
        CancellationToken cancellationToken)
    {
        var listing = await _tool.RunAsync("list", cancellationToken).ConfigureAwait(false);

        if (listing.Forbidden)
        {
            throw new InvalidOperationException(
                "Встроенный захват требует прав администратора: драйвер pktmon "
                + "разговаривает только с ними. Перезапустите продукт от имени администратора.");
        }

        var components = PktMonComponents.WithMac(PktMonComponents.Parse(listing.Output), adapter.MacAddress);

        // Компонент не нашёлся — слушаем все сетевые карты. Это шумнее (дубли
        // снимаются ниже), но лучше, чем отказать: имя карты в списке pktmon
        // и в системе совпадает не на всякой машине, а MAC — не всегда.
        var which = components.Count > 0
            ? string.Join(' ', components.Select(c => c.Id.ToString(CultureInfo.InvariantCulture)))
            : "nics";

        await ArrangeAsync(options, cancellationToken).ConfigureAwait(false);

        var etl = Path.Combine(work, "журнал.etl");
        var started = await _tool
            .RunAsync($"start -c --comp {which} --pkt-size 0 --file-name \"{etl}\"", cancellationToken)
            .ConfigureAwait(false);

        if (!started.Ok)
        {
            throw new InvalidOperationException(
                "Встроенный захват не запустился. " + Trim(started.Output));
        }

        try
        {
            await Task.Delay(options.Duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Оператор передумал ждать. Услышанное к этой минуте — всё равно
            // услышанное: останавливаемся и разбираем, а не выбрасываем.
        }

        // CancellationToken.None намеренно: остановить захват и убрать за собой
        // нужно и после отмены — иначе драйвер останется писать в удалённый файл.
        await _tool.RunAsync("stop", CancellationToken.None).ConfigureAwait(false);

        var pcap = Path.Combine(work, "журнал.pcapng");
        var converted = await _tool
            .RunAsync($"etl2pcap \"{etl}\" -o \"{pcap}\"", CancellationToken.None)
            .ConfigureAwait(false);

        if (!converted.Ok || !File.Exists(pcap))
        {
            throw new InvalidOperationException(
                "Журнал захвата не преобразовался. " + Trim(converted.Output));
        }

        return [.. PcapNgReader.ReadFile(pcap).Take(options.FrameLimit)];
    }

    /// <summary>
    /// Фильтры ставятся на драйвер, а не в наш код.
    /// </summary>
    /// <remarks>
    /// Отбирать в пользовательском пространстве значило бы протащить через границу
    /// весь трафик сегмента — и по нагрузке, и по смыслу это другое действие.
    /// <para>
    /// Чужие фильтры pktmon при этом снимаются: оставить их значило бы слушать
    /// заодно и то, чего мы не просили. Фильтры живут до перезагрузки и своих,
    /// поставленных руками, у продукта быть не может.
    /// </para>
    /// </remarks>
    private async Task ArrangeAsync(CaptureOptions options, CancellationToken cancellationToken)
    {
        await _tool.RunAsync("filter remove", cancellationToken).ConfigureAwait(false);

        if (options.Neighbors)
        {
            await _tool.RunAsync($"filter add lldp -d {LldpType}", cancellationToken).ConfigureAwait(false);
            await _tool.RunAsync($"filter add cdp -m {CdpDestination}", cancellationToken).ConfigureAwait(false);
        }

        if (options.Dhcp)
        {
            await _tool
                .RunAsync($"filter add dhcp -t UDP -p {PktMonTool.Port(DhcpServerPort)}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Убрать за собой: снять фильтры и выгрузить драйвер.
    /// </summary>
    /// <remarks>
    /// Выгрузка — не вежливость, а исправление того, что продукт сам же и натворил.
    /// Пока драйвер загружен, в списке сетевых интерфейсов Windows живут служебные
    /// фильтры NDIS с MAC-адресами настоящих карт: после первого же прослушивания
    /// оператор увидел бы в выборе адаптера «Ethernet 4-WFP Native MAC Layer
    /// LightWeight Filter-0000» вместо своей карты. Поймано боевым прогоном.
    /// <para>
    /// Отказ выгрузки проглатывается: драйвер мог быть нужен кому-то ещё, и это
    /// его право — а прослушивание к этому моменту уже состоялось.
    /// </para>
    /// </remarks>
    private async Task TidyAsync()
    {
        await _tool.RunAsync("filter remove", CancellationToken.None).ConfigureAwait(false);
        await _tool.RunAsync("unload", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Разбор услышанного: снятие дублей и опознание кадров.</summary>
    private static CaptureResult Understand(
        CaptureAdapter adapter,
        CaptureOptions options,
        IReadOnlyList<CapturedFrame> frames,
        DateTimeOffset startedUtc)
    {
        var neighbors = new List<LinkNeighbor>();
        var sightings = new List<DhcpSighting>();
        var echoes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var seen = 0;
        var unparsed = 0;

        foreach (var frame in frames)
        {
            if (Repeat(echoes, frame))
            {
                continue;
            }

            seen++;

            var finding = FrameParser.Parse(frame.Bytes, frame.ObservedUtc, adapter.DisplayName);

            if (finding is null)
            {
                unparsed++;

                continue;
            }

            if (options.Neighbors && finding.Neighbor is { } neighbor && !Known(neighbors, neighbor))
            {
                neighbors.Add(neighbor);
            }

            if (options.Dhcp && finding.Dhcp is { } sighting)
            {
                sightings.Add(sighting);
            }
        }

        return new CaptureResult
        {
            Adapter = adapter,
            StartedUtc = startedUtc,
            Duration = DateTimeOffset.UtcNow - startedUtc,
            FramesSeen = seen,
            Unparsed = unparsed,
            Neighbors = neighbors,
            Dhcp = new DhcpFinding { Sightings = sightings },
        };
    }

    private static bool Repeat(Dictionary<string, DateTimeOffset> echoes, CapturedFrame frame)
    {
        var key = Convert.ToHexString(frame.Bytes);

        if (echoes.TryGetValue(key, out var when) && frame.ObservedUtc - when < SameFrame)
        {
            return true;
        }

        echoes[key] = frame.ObservedUtc;

        return false;
    }

    /// <summary>Один сосед объявляется каждые полминуты — складывать объявления незачем.</summary>
    private static bool Known(List<LinkNeighbor> known, LinkNeighbor candidate) =>
        known.Exists(n => string.Equals(n.RemoteName, candidate.RemoteName, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(n.RemotePort, candidate.RemotePort, StringComparison.OrdinalIgnoreCase)
                          && n.Protocol == candidate.Protocol);

    private static CaptureResult Empty(CaptureAdapter adapter, DateTimeOffset startedUtc) => new()
    {
        Adapter = adapter,
        StartedUtc = startedUtc,
        Duration = TimeSpan.Zero,
        FramesSeen = 0,
        Unparsed = 0,
        Neighbors = [],
        Dhcp = new DhcpFinding { Sightings = [] },
    };

    private static string Trim(string output)
    {
        var line = output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();

        return string.IsNullOrEmpty(line) ? "Причина не названа." : line;
    }

    private static void Forget(DirectoryInfo work)
    {
        try
        {
            work.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Журнал остался во временной папке — система приберёт его сама.
            // Падать из-за этого после удачного прослушивания нельзя.
        }
    }
}
