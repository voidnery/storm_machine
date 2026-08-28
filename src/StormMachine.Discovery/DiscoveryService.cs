using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;

namespace StormMachine.Discovery;

/// <summary>
/// Обнаружение узлов в диапазоне адресов.
/// </summary>
/// <remarks>
/// Порядок проб выбран не по удобству, а по тому, кто как молчит.
/// <list type="number">
/// <item>ICMP — самый дешёвый вопрос, но рабочая станция Windows на него не отвечает.</item>
/// <item>TCP к нескольким частым портам — ловит тех, кто закрылся от ICMP брандмауэром.</item>
/// <item>Таблица ARP после сканирования — ловит тех, кто молчит и на то, и на другое.
/// Это главный приём: узел может дропать любой трафик третьего уровня, но ответить
/// на ARP он обязан, иначе с ним нельзя разговаривать вовсе. Наши же пробы и заставляют
/// систему выполнить разрешение адреса, а результат остаётся в таблице.</item>
/// </list>
/// <para>
/// Отсюда же берутся MAC-адреса — <b>без прав администратора и без драйвера захвата</b>
/// (<c>R-03</c>). Именно это делает инвентарь частью уровня 0.
/// </para>
/// </remarks>
public sealed class DiscoveryService(
    IArpResolver arp,
    IOuiCatalog oui,
    INetworkEnvironment environment) : IDiscoveryService
{
    /// <summary>
    /// Порты, по которым проверяются узлы, промолчавшие на ICMP.
    /// </summary>
    /// <remarks>
    /// Набор намеренно короткий. Это не сканирование портов, а проверка «жив ли узел»:
    /// шесть попыток на адрес — вопрос о присутствии, а не разведка сервисов.
    /// Выбраны те, что закрывают основные семейства: Windows (445, 135), сетевое
    /// оборудование и веб-панели (80, 443), Linux (22), устройства Apple (62078).
    /// </remarks>
    private static readonly int[] CommonPorts = [445, 135, 80, 443, 22, 62078];

    /// <summary>Предел ожидания обратного DNS. Отсутствие записи — норма.</summary>
    private static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Пауза между окончанием опроса и чтением таблицы ARP.
    /// </summary>
    /// <remarks>
    /// Разрешение адреса не мгновенно: последние наши пакеты ещё в пути, ответы ARP
    /// на них ещё не пришли, и таблица, прочитанная сразу, не содержит части узлов.
    /// Заметно на быстром сканировании: без паузы улов ARP падал вдвое. Полсекунды
    /// на трёх секундах работы — приемлемая цена за узлы, которых иначе не видно вовсе.
    /// </remarks>
    private static readonly TimeSpan ArpSettleDelay = TimeSpan.FromMilliseconds(500);

    private readonly IArpResolver _arp = arp ?? throw new ArgumentNullException(nameof(arp));
    private readonly IOuiCatalog _oui = oui ?? throw new ArgumentNullException(nameof(oui));
    private readonly INetworkEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public async Task<DiscoveryScan> ScanAsync(
        DiscoveryRequest request,
        Action<DiscoveryProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedUtc = DateTimeOffset.UtcNow;
        var adapter = _environment.GetPrimaryAdapter();
        var addresses = request.Range.Enumerate().ToList();

        var findings = new ConcurrentDictionary<string, Finding>(StringComparer.Ordinal);
        var probed = 0;
        var cancelled = false;

        using var gate = new SemaphoreSlim(Math.Max(1, request.Parallelism));

        var sweep = addresses.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var finding = await ReachAsync(address, request, cancellationToken).ConfigureAwait(false);

                if (finding is not null)
                {
                    findings[address.ToString()] = finding;
                }

                var done = Interlocked.Increment(ref probed);
                onProgress?.Invoke(new DiscoveryProgress
                {
                    Probed = done,
                    Total = addresses.Count,
                    Found = findings.Count,
                });
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(sweep).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        // Таблица читается ПОСЛЕ проб: наши же пакеты заставили систему разрешить
        // адреса, и теперь в таблице есть MAC даже тех узлов, что промолчали на всё.
        // Пауза перед чтением — чтобы дать ответам ARP дойти.
        try
        {
            await Task.Delay(ArpSettleDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        var arpTable = _arp.ReadTable();
        var devices = await BuildAsync(request, findings, arpTable, adapter, startedUtc, cancellationToken)
            .ConfigureAwait(false);

        return new DiscoveryScan
        {
            Id = Guid.NewGuid(),
            Range = request.Range.Text,
            InterfaceName = adapter?.Name ?? "неизвестен",
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Probed = probed,
            WasCancelled = cancelled || cancellationToken.IsCancellationRequested,
            Devices = devices,
        };
    }

    /// <summary>Достучался ли до узла хоть кто-нибудь.</summary>
    private static async Task<Finding?> ReachAsync(
        IPAddress address,
        DiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        using var ping = new Ping();

        try
        {
            var reply = await ping
                .SendPingAsync(address, TimeSpan.FromMilliseconds(request.TimeoutMs), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (reply.Status == IPStatus.Success)
            {
                return new Finding(EvidenceSource.IcmpEcho, null);
            }
        }
        catch (Exception ex) when (ex is PingException or SocketException)
        {
            // Недоступная сеть или отказ стека — узел просто не найден этим способом.
        }

        if (!request.ProbeCommonPorts)
        {
            return null;
        }

        var port = await FirstOpenPortAsync(address, cancellationToken).ConfigureAwait(false);

        return port is { } open ? new Finding(EvidenceSource.TcpConnect, open) : null;
    }

    /// <summary>
    /// Первый порт, принявший соединение.
    /// </summary>
    /// <remarks>
    /// Порты пробуются одновременно, а первый же ответ отменяет остальные попытки:
    /// цель — узнать, что узел есть, а не составить список его служб.
    /// </remarks>
    private static async Task<int?> FirstOpenPortAsync(IPAddress address, CancellationToken cancellationToken)
    {
        // Короче общего таймаута: закрытый порт отвечает отказом сразу,
        // а молчащий всё равно ничего не скажет.
        const int PortTimeoutMs = 400;

        using var found = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var attempts = CommonPorts.Select(async port =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(address, port, found.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(PortTimeoutMs), found.Token)
                    .ConfigureAwait(false);

                return (int?)port;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                return null;
            }
        }).ToList();

        foreach (var result in await Task.WhenAll(attempts).ConfigureAwait(false))
        {
            if (result is { } port)
            {
                await found.CancelAsync().ConfigureAwait(false);
                return port;
            }
        }

        return null;
    }

    /// <summary>
    /// Собирает устройства из находок, таблицы ARP и обогащения.
    /// </summary>
    /// <remarks>
    /// Обогащение идёт <b>параллельно</b>, с тем же ограничением темпа, что и опрос.
    /// Первая версия делала его в цикле, и на сети из сотни узлов сканирование заняло
    /// восемьдесят секунд вместо пяти: обратный DNS и NetBIOS ждут ответа сотнями
    /// миллисекунд, и сто таких ожиданий подряд складываются в минуты.
    /// </remarks>
    private async Task<List<Device>> BuildAsync(
        DiscoveryRequest request,
        ConcurrentDictionary<string, Finding> findings,
        IReadOnlyDictionary<string, string> arpTable,
        NetworkAdapter? adapter,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        var candidates = new SortedDictionary<uint, string>();

        foreach (var address in findings.Keys)
        {
            candidates[IpAddressOrder.Of(address)] = address;
        }

        // Узел, ответивший только на ARP, — самый ценный улов сканирования:
        // он молчит на всё третьем уровне, и обычный ping-sweep его не находит.
        foreach (var address in arpTable.Keys)
        {
            if (IPAddress.TryParse(address, out var parsed) && request.Range.Contains(parsed))
            {
                candidates[IpAddressOrder.Of(address)] = address;
            }
        }

        var gateways = adapter?.Gateways;
        var gateway = gateways is { Count: > 0 } ? gateways[0] : null;

        var ordered = candidates.Values.ToList();
        var built = new Device[ordered.Count];

        using var gate = new SemaphoreSlim(Math.Max(1, request.Parallelism));

        var work = ordered.Select(async (address, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                built[index] = await DescribeAsync(
                    address,
                    request,
                    findings,
                    arpTable,
                    gateway,
                    observedUtc,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        return [.. built];
    }

    private async Task<Device> DescribeAsync(
        string address,
        DiscoveryRequest request,
        ConcurrentDictionary<string, Finding> findings,
        IReadOnlyDictionary<string, string> arpTable,
        string? gateway,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        var evidence = new List<Evidence>(6);
        findings.TryGetValue(address, out var finding);

        if (finding is not null)
        {
            evidence.Add(Evidence.Of(finding.Source, EvidenceKind.Alive, "да", observedUtc));

            if (finding.OpenPort is { } port)
            {
                evidence.Add(Evidence.Of(
                    EvidenceSource.TcpConnect,
                    EvidenceKind.OpenPort,
                    port.ToString(CultureInfo.InvariantCulture),
                    observedUtc));
            }
        }

        var mac = ResolveMac(address, arpTable, evidence, observedUtc);

        if (mac is not null && _oui.Lookup(mac) is { } vendor)
        {
            evidence.Add(Evidence.Of(EvidenceSource.Oui, EvidenceKind.Vendor, vendor, observedUtc));
        }

        if (string.Equals(address, gateway, StringComparison.Ordinal))
        {
            evidence.Add(Evidence.Of(EvidenceSource.ArpTable, EvidenceKind.Role, "шлюз", observedUtc));
        }

        if (request.ResolveNames)
        {
            await AddNamesAsync(address, evidence, observedUtc, cancellationToken).ConfigureAwait(false);
        }

        return Device.FromEvidence(
            address,
            evidence,
            firstSeenUtc: observedUtc,
            lastSeenUtc: observedUtc,
            isOnline: evidence.Exists(e => e.Kind == EvidenceKind.Alive));
    }

    private string? ResolveMac(
        string address,
        IReadOnlyDictionary<string, string> arpTable,
        List<Evidence> evidence,
        DateTimeOffset observedUtc)
    {
        if (arpTable.TryGetValue(address, out var known))
        {
            evidence.Add(Evidence.Of(EvidenceSource.ArpTable, EvidenceKind.MacAddress, known, observedUtc));

            // Свежая запись в таблице ARP означает, что узел отвечает на втором уровне,
            // даже если промолчал на всё остальное.
            if (!evidence.Exists(e => e.Kind == EvidenceKind.Alive))
            {
                evidence.Add(Evidence.Of(EvidenceSource.ArpTable, EvidenceKind.Alive, "да", observedUtc));
            }

            return known;
        }

        if (IPAddress.TryParse(address, out var parsed) && _arp.Resolve(parsed) is { } asked)
        {
            evidence.Add(Evidence.Of(EvidenceSource.ArpRequest, EvidenceKind.MacAddress, asked, observedUtc));

            if (!evidence.Exists(e => e.Kind == EvidenceKind.Alive))
            {
                evidence.Add(Evidence.Of(EvidenceSource.ArpRequest, EvidenceKind.Alive, "да", observedUtc));
            }

            return asked;
        }

        return null;
    }

    /// <summary>
    /// Спрашивает имя узла двумя способами сразу.
    /// </summary>
    /// <remarks>
    /// Обратный DNS и NetBIOS отвечают независимо и обычно на разных узлах: у сервера
    /// есть запись PTR, у рабочей станции Windows — нет, зато она называет себя сама.
    /// Ждать их по очереди значит удваивать время там, где можно ждать один раз.
    /// </remarks>
    private static async Task AddNamesAsync(
        string address,
        List<Evidence> evidence,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out var parsed))
        {
            return;
        }

        var dns = ReverseDnsAsync(parsed, cancellationToken);
        var netbios = NetbiosNameQuery.AskAsync(parsed, cancellationToken);

        await Task.WhenAll(dns, netbios).ConfigureAwait(false);

        if (await dns.ConfigureAwait(false) is { } hostName)
        {
            evidence.Add(Evidence.Of(EvidenceSource.ReverseDns, EvidenceKind.HostName, hostName, observedUtc));
        }

        // NetBIOS отвечает там, где обратной зоны нет вовсе: в офисной сети это
        // единственный способ узнать имя рабочей станции Windows, не спрашивая сервер.
        if (await netbios.ConfigureAwait(false) is { } name)
        {
            evidence.Add(Evidence.Of(EvidenceSource.Netbios, EvidenceKind.HostName, name, observedUtc));
        }
    }

    private static async Task<string?> ReverseDnsAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address)
                .WaitAsync(ReverseDnsTimeout, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Обратной записи нет — обычное дело в локальной сети.
            return null;
        }
    }

    private sealed record Finding(EvidenceSource Source, int? OpenPort);
}
