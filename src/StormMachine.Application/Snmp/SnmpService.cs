using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;

namespace StormMachine.Application.Snmp;

/// <summary>Чем и как удалось опросить устройство.</summary>
/// <param name="Credential">Подошедший набор учётных данных.</param>
/// <param name="System">Системная группа устройства.</param>
public sealed record SnmpReach(SnmpCredential Credential, SnmpSystem System);

/// <summary>Порт вместе с посчитанной нагрузкой или причиной, по которой её нет.</summary>
public sealed record PortLoad(SnmpInterface Interface, InterfaceLoad? Load, LoadRefusal Refusal);

/// <summary>
/// Опрос оборудования по SNMP.
/// </summary>
/// <remarks>
/// <b>Перебор учётных данных здесь — не подбор.</b> Пробуются только те наборы,
/// которые оператор завёл сам, и только против узла, который он назвал. Ни словарей,
/// ни перебора сообществ, ни обхода подсети «а вдруг где-то public» в продукте нет
/// и не будет: это ровно та граница, за которой инструмент диагностики становится
/// инструментом взлома (docs/01-analysis.md §1.4).
/// <para>
/// Порядок перебора задаёт сам оператор полем <see cref="SnmpCredential.Order"/>:
/// на объекте, где ядро отвечает по v3, а доступ — по v2c, важно, чтобы первым
/// пробовался тот набор, который чаще подходит, а не тот, что завели раньше.
/// </para>
/// </remarks>
public sealed class SnmpService(ISnmpClient client, ISnmpCredentialStore credentials)
{
    private readonly ISnmpClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ISnmpCredentialStore _credentials = credentials
        ?? throw new ArgumentNullException(nameof(credentials));

    /// <summary>Есть ли вообще чем опрашивать.</summary>
    public async Task<bool> HasCredentialsAsync(CancellationToken cancellationToken = default) =>
        (await _credentials.ListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <summary>
    /// Подбирает подходящий набор из заведённых оператором.
    /// </summary>
    /// <remarks>
    /// Возвращает <c>null</c>, если не подошёл ни один. Различить «SNMP выключен»
    /// и «учётные данные не те» снаружи нельзя: устройство, отвергающее запрос,
    /// в большинстве случаев просто молчит — так предписывает RFC 3414 §3.2 ради
    /// того, чтобы молчание не подсказывало подбирающему.
    /// </remarks>
    public async Task<SnmpReach?> ProbeAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        foreach (var stored in await Ordered(cancellationToken).ConfigureAwait(false))
        {
            var credential = await _credentials.GetAsync(stored.Id, cancellationToken).ConfigureAwait(false);

            if (credential is null)
            {
                continue;
            }

            try
            {
                var system = await _client.GetSystemAsync(host, credential, cancellationToken)
                    .ConfigureAwait(false);

                if (system is not null)
                {
                    return new SnmpReach(credential, system);
                }
            }
            catch (SnmpException ex) when (ex.Reason is SnmpFailure.NoAnswer or SnmpFailure.Rejected)
            {
                // Этот набор не подошёл — пробуем следующий. Прочие причины
                // (нет такого узла, искажённый ответ) относятся к узлу, а не к набору,
                // и перебирать дальше бессмысленно.
            }
        }

        return null;
    }

    /// <summary>
    /// Собирает согласованный снимок устройства.
    /// </summary>
    /// <remarks>
    /// Порты, соседи и таблица пересылки читаются подряд и складываются в один объект
    /// с одной отметкой времени. Разнести их по разным запросам к базе значило бы
    /// собрать карту из разных состояний сети — на живом коммутаторе таблица пересылки
    /// меняется каждые несколько минут.
    /// <para>
    /// Отсутствие соседей или таблицы пересылки — не ошибка: у маршрутизатора нет
    /// второго уровня, у неуправляемого коммутатора нет ничего. Продукт записывает
    /// то, что есть, и не считает пустоту отказом.
    /// </para>
    /// </remarks>
    public async Task<SnmpDevice> InspectAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(credential);

        var system = await _client.GetSystemAsync(host, credential, cancellationToken).ConfigureAwait(false)
            ?? throw new SnmpException(
                $"Узел {host} не отдал системную группу.",
                SnmpFailure.NoAnswer);

        var interfaces = await _client.GetInterfacesAsync(host, credential, cancellationToken)
            .ConfigureAwait(false);

        var neighbors = await Optional(
            () => _client.GetNeighborsAsync(host, credential, cancellationToken)).ConfigureAwait(false);

        var forwarding = await Optional(
            () => _client.GetForwardingAsync(host, credential, cancellationToken)).ConfigureAwait(false);

        return new SnmpDevice
        {
            Address = host,
            System = system,
            ObservedUtc = DateTimeOffset.UtcNow,
            Interfaces = interfaces,
            Neighbors = Named(neighbors, interfaces),
            Forwarding = Named(forwarding, interfaces),
            Credential = credential.Name,
        };
    }

    /// <summary>
    /// Меряет нагрузку портов: два снимка счётчиков с паузой между ними.
    /// </summary>
    /// <remarks>
    /// Один снимок не значит ничего — счётчики растут от загрузки устройства.
    /// Пауза выбирается оператором, но продукт обязан сказать, когда она опасна:
    /// на 32-разрядных счётчиках гигабитный порт переполняется за 34 секунды.
    /// </remarks>
    public async Task<IReadOnlyList<PortLoad>> MeasureAsync(
        string host,
        SnmpCredential credential,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(credential);

        var interfaces = await _client.GetInterfacesAsync(host, credential, cancellationToken)
            .ConfigureAwait(false);

        var before = await _client.GetCountersAsync(host, credential, cancellationToken).ConfigureAwait(false);

        await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

        var after = await _client.GetCountersAsync(host, credential, cancellationToken).ConfigureAwait(false);

        var first = before.ToDictionary(c => c.Index);
        var result = new List<PortLoad>();

        foreach (var port in interfaces)
        {
            var last = after.FirstOrDefault(c => c.Index == port.Index);

            if (last is null || !first.TryGetValue(port.Index, out var start))
            {
                result.Add(new PortLoad(port, null, LoadRefusal.BadInterval));

                continue;
            }

            var load = InterfaceLoadCalculator.Between(
                start,
                last,
                port.SpeedBitsPerSecond,
                out var refusal);

            result.Add(new PortLoad(port, load, refusal));
        }

        return result;
    }

    /// <summary>
    /// Предупреждение о слишком редком опросе.
    /// </summary>
    /// <remarks>
    /// Считается до измерения, а не после: сказать «данные негодны» уже после того,
    /// как человек прождал минуту, — половина пользы.
    /// </remarks>
    public static string? IntervalWarning(
        IReadOnlyList<SnmpInterface> interfaces,
        TimeSpan interval,
        bool highCapacity)
    {
        ArgumentNullException.ThrowIfNull(interfaces);

        if (highCapacity)
        {
            return null;
        }

        var risky = interfaces
            .Where(i => i.SpeedBitsPerSecond > 0
                        && !InterfaceLoadCalculator.IsIntervalSafe(interval, i.SpeedBitsPerSecond, false))
            .ToList();

        if (risky.Count == 0)
        {
            return null;
        }

        var fastest = risky.Max(i => i.SpeedBitsPerSecond);
        var horizon = InterfaceLoadCalculator.WrapHorizon(fastest)!.Value;

        return $"Счётчики 32-разрядные, а пауза {Round(interval)}. На {risky.Count} порт(ах) "
               + $"счётчик переполняется за {Round(horizon)} — значения будут отброшены. "
               + "Помогает версия v2c: у неё счётчики 64-разрядные.";
    }

    private async Task<IReadOnlyList<SnmpCredential>> Ordered(CancellationToken cancellationToken)
    {
        var all = await _credentials.ListAsync(cancellationToken).ConfigureAwait(false);

        return [.. all.OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>Ветка, которой у устройства может не быть. Пустота — не отказ.</summary>
    private static async Task<IReadOnlyList<T>> Optional<T>(Func<Task<IReadOnlyList<T>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (SnmpException ex) when (ex.Reason is SnmpFailure.NoSuchObject or SnmpFailure.NoAnswer)
        {
            return [];
        }
    }

    /// <summary>Подставляет имена портов к соседям: <c>ifIndex</c> человеку ничего не говорит.</summary>
    private static IReadOnlyList<LinkNeighbor> Named(
        IReadOnlyList<LinkNeighbor> neighbors,
        IReadOnlyList<SnmpInterface> interfaces)
    {
        var names = interfaces.ToDictionary(i => i.Index, i => i.Name);

        return
        [
            .. neighbors.Select(n => n.LocalPort is null && names.TryGetValue(n.LocalIfIndex, out var name)
                ? n with { LocalPort = name }
                : n),
        ];
    }

    private static IReadOnlyList<ForwardingEntry> Named(
        IReadOnlyList<ForwardingEntry> entries,
        IReadOnlyList<SnmpInterface> interfaces)
    {
        var names = interfaces.ToDictionary(i => i.Index, i => i.Name);

        return
        [
            .. entries.Select(e => e.PortName is null && names.TryGetValue(e.IfIndex, out var name)
                ? e with { PortName = name }
                : e),
        ];
    }

    private static string Round(TimeSpan span) => span.TotalSeconds < 60
        ? $"{span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} с"
        : $"{span.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} мин";
}
