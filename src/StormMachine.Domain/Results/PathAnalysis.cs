using StormMachine.Domain.Measurements;

namespace StormMachine.Domain.Results;

/// <summary>Что известно про один хоп маршрута.</summary>
public sealed record HopStatistics
{
    /// <summary>Номер хопа, он же TTL.</summary>
    public required int Hop { get; init; }

    /// <summary>Адрес, отвечавший последним. Пусто — хоп молчит.</summary>
    public string? Address { get; init; }

    /// <summary>
    /// Все адреса, отвечавшие на этом хопе за время наблюдения.
    /// </summary>
    /// <remarks>
    /// Больше одного означает либо смену маршрута, либо балансировку по нескольким
    /// каналам. Различить их по одному наблюдению нельзя, поэтому показываются все.
    /// </remarks>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    public required int Sent { get; init; }

    public required int Received { get; init; }

    public required LatencyStatistics Statistics { get; init; }

    /// <summary>
    /// Оценка пригодности канала до этого хопа для голоса.
    /// </summary>
    /// <remarks>
    /// На транзитных хопах считается только по задержке и дрожанию, без потерь. Потери
    /// на транзитном узле почти всегда означают ограничение частоты его собственных
    /// ответов, а не потерю транзитного трафика, — и подставлять их в E-модель значило бы
    /// выводить «непригодно» для канала, по которому голос идёт прекрасно. На конечном
    /// узле потери учитываются полностью: там они настоящие.
    /// </remarks>
    public required VoiceQuality Voice { get; init; }

    public int Lost => Sent - Received;

    public double LossPercent => Sent == 0 ? 0 : Lost * 100.0 / Sent;

    public bool IsSilent => Received == 0;

    /// <summary>Хоп — конечная точка маршрута.</summary>
    public bool IsDestination { get; init; }

    /// <summary>
    /// На этом хопе цель отвечала, но конечной точкой он не является.
    /// </summary>
    /// <remarks>
    /// Длина пути непостоянна: часть пакетов доходит до цели раньше. Показывать долю
    /// оставшихся как «потери» нельзя — они не потеряны, а ушли длинным путём.
    /// Отсюда прочерк в колонке потерь: цифра здесь означала бы не то, что читается.
    /// </remarks>
    public bool IsEarlyDestination { get; init; }

    /// <summary>Доля пакетов, дошедших до цели с этого хопа.</summary>
    public double ShortPathPercent => Sent == 0 ? 0 : Received * 100.0 / Sent;
}

/// <summary>Смена отвечающего узла на хопе.</summary>
public sealed record RouteChange(int Hop, string From, string To);

/// <summary>
/// Разбор маршрута: хопы, смены пути и место, где начинается деградация.
/// </summary>
/// <remarks>
/// Главное здесь — не таблица, а правило определения точки деградации. Промежуточные
/// узлы сплошь и рядом не отвечают на ICMP или отвечают с задержкой: это защита
/// от нагрузки, а не проблема канала. Считать такие хопы неисправными — самая частая
/// ошибка чтения traceroute, и инструмент обязан её не повторять.
/// </remarks>
public sealed record PathAnalysis
{
    /// <summary>Порог, начиная с которого потери считаются значимыми.</summary>
    public const double SignificantLossPercent = 5.0;

    public required IReadOnlyList<HopStatistics> Hops { get; init; }

    public required bool DestinationReached { get; init; }

    public IReadOnlyList<RouteChange> RouteChanges { get; init; } = [];

    /// <summary>
    /// Хоп, начиная с которого потери держатся до конца маршрута.
    /// </summary>
    /// <remarks>
    /// <c>null</c> означает, что устойчивой деградации нет — даже если отдельные хопы
    /// показывают потери.
    /// </remarks>
    public HopStatistics? DegradationPoint { get; init; }

    /// <summary>Число хопов, не ответивших ни разу.</summary>
    public int SilentHops => Hops.Count(h => h.IsSilent);

    /// <summary>
    /// Хопы до конечного, на которых цель тоже иногда отвечала.
    /// </summary>
    /// <remarks>
    /// Наблюдается регулярно и означает не ошибку измерения, а переменную длину пути:
    /// в туннеле MPLS без переноса TTL весь туннель считается одним хопом, и при смене
    /// маршрута внутри него цель оказывается то ближе, то дальше. Проверено меткой
    /// в полезной нагрузке: на низких TTL возвращаются наши собственные пакеты,
    /// а не чужие ответы.
    /// <para>
    /// Показывать такие хопы как конечную точку нельзя: у них почти стопроцентные
    /// «потери» — на деле это доля пакетов, ушедших по длинному пути. Отсюда правило:
    /// конечная точка — <b>последний</b> хоп, ответивший целью.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> EarlyDestinationHops { get; init; } = [];

    /// <summary>Итоговое качество маршрута — по конечному узлу, а не по худшему хопу.</summary>
    public VoiceQuality DestinationVoice =>
        Hops.LastOrDefault(h => h.IsDestination)?.Voice ?? VoiceQuality.Unknown;

    public static PathAnalysis Compute(IReadOnlyList<Sample> samples, string? destinationAddress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var byHop = new SortedDictionary<int, List<Sample>>();

        foreach (var sample in samples)
        {
            if (sample.Group is not { } hop)
            {
                continue;
            }

            if (!byHop.TryGetValue(hop, out var bucket))
            {
                bucket = [];
                byHop[hop] = bucket;
            }

            bucket.Add(sample);
        }

        var hops = new List<HopStatistics>(byHop.Count);
        var changes = new List<RouteChange>();

        foreach (var (hop, bucket) in byHop)
        {
            var addresses = new List<string>();
            string? last = null;

            foreach (var sample in bucket)
            {
                if (string.IsNullOrEmpty(sample.RespondedBy))
                {
                    continue;
                }

                if (last is not null && !string.Equals(last, sample.RespondedBy, StringComparison.Ordinal))
                {
                    changes.Add(new RouteChange(hop, last, sample.RespondedBy));
                }

                last = sample.RespondedBy;

                if (!addresses.Contains(sample.RespondedBy, StringComparer.Ordinal))
                {
                    addresses.Add(sample.RespondedBy);
                }
            }

            var received = bucket.Count(s => s.IsSuccess);
            var statistics = LatencyStatistics.Compute(bucket);

            hops.Add(new HopStatistics
            {
                Hop = hop,
                Address = last,
                Addresses = addresses,
                Sent = bucket.Count,
                Received = received,
                Statistics = statistics,

                // Заполняется после того, как станет известна конечная точка:
                // на транзитном хопе потери в оценку не входят.
                Voice = VoiceQuality.Unknown,
                IsDestination = false,
            });
        }

        return Finish(hops, changes, destinationAddress);
    }

    /// <summary>
    /// Отмечает конечную точку и досчитывает то, что от неё зависит.
    /// </summary>
    /// <remarks>
    /// Общий хвост для разбора по сэмплам и по агрегатам: правило конечной точки
    /// одно, и расходиться этим двум путям нельзя.
    /// </remarks>
    private static PathAnalysis Finish(
        List<HopStatistics> hops,
        List<RouteChange> changes,
        string? destinationAddress)
    {
        var matches = new List<int>();

        if (destinationAddress is not null)
        {
            for (var i = 0; i < hops.Count; i++)
            {
                if (hops[i].Addresses.Contains(destinationAddress, StringComparer.Ordinal))
                {
                    matches.Add(i);
                }
            }
        }

        // Конечная точка — последний хоп, ответивший целью. Более ранние совпадения
        // означают переменную длину пути, а не вторую цель.
        var terminal = matches.Count > 0 ? matches[^1] : hops.Count - 1;

        for (var i = 0; i < hops.Count; i++)
        {
            var hop = hops[i];
            var isDestination = i == terminal && matches.Count > 0;

            hops[i] = hop with
            {
                IsDestination = isDestination,
                IsEarlyDestination = !isDestination && matches.Contains(i),
                Voice = VoiceQualityEstimate.Estimate(hop.Statistics, isDestination ? hop.LossPercent : 0),
            };
        }

        return new PathAnalysis
        {
            Hops = hops,
            DestinationReached = matches.Count > 0,
            RouteChanges = changes,
            DegradationPoint = FindDegradationPoint(hops, terminal),
            EarlyDestinationHops = [.. matches.Take(Math.Max(0, matches.Count - 1)).Select(i => hops[i].Hop)],
        };
    }

    /// <summary>
    /// Восстанавливает разбор маршрута из сохранённых агрегатов.
    /// </summary>
    /// <remarks>
    /// Нужно для отчётов по старым прогонам: политика хранения удаляет сырые сэмплы,
    /// а агрегаты по рядам остаются навсегда. Без этого метода отчёт годовой давности
    /// показывал бы таблицу без вывода — то есть ровно то, ради чего его открывают.
    /// <para>
    /// Что не восстанавливается — смены маршрута: они живут в истории адресов, а её
    /// в агрегатах нет. Список остаётся пустым, и это честнее, чем показать ноль смен
    /// там, где их просто не по чему посчитать.
    /// </para>
    /// </remarks>
    public static PathAnalysis FromSeries(
        IReadOnlyList<SeriesStatistics> series,
        string? destinationAddress = null)
    {
        ArgumentNullException.ThrowIfNull(series);

        var hops = new List<HopStatistics>(series.Count);

        foreach (var row in series)
        {
            if (!TryParseHop(row.Key, out var hop))
            {
                continue;
            }

            var address = row.Label is null or "*" or "" ? null : row.Label;

            hops.Add(new HopStatistics
            {
                Hop = hop,
                Address = address,
                Addresses = address is null ? [] : [address],
                Sent = row.SentCount,
                Received = row.SuccessCount,
                Statistics = row.Statistics,
                Voice = VoiceQuality.Unknown,
                IsDestination = false,
            });
        }

        hops.Sort((a, b) => a.Hop.CompareTo(b.Hop));

        return Finish(hops, [], destinationAddress);
    }

    /// <summary>Ключ ряда хопа имеет вид <c>hop:N</c>; всё прочее — не хоп.</summary>
    private static bool TryParseHop(string key, out int hop)
    {
        const string Prefix = "hop:";

        hop = 0;

        return key.StartsWith(Prefix, StringComparison.Ordinal)
               && int.TryParse(
                   key.AsSpan(Prefix.Length),
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out hop);
    }

    /// <summary>
    /// Ищет хоп, с которого потери начинаются и держатся до конца маршрута.
    /// </summary>
    /// <remarks>
    /// Потери на одном промежуточном хопе, исчезающие дальше по маршруту, — это
    /// ограничение скорости ответов на самом узле, а не потеря трафика. Значение имеет
    /// только деградация, дошедшая до конечной точки.
    /// <para>
    /// Молчащие хопы пропускаются: узел, не отвечающий на ICMP вовсе, ничего не сообщает
    /// о судьбе транзитного трафика.
    /// </para>
    /// </remarks>
    private static HopStatistics? FindDegradationPoint(List<HopStatistics> hops, int terminal)
    {
        if (hops.Count == 0 || terminal < 0 || terminal >= hops.Count)
        {
            return null;
        }

        // Отсчёт идёт от конечной точки, а не от последней строки таблицы: за целью
        // могут стоять хопы, отвечавшие раньше при другой длине пути.
        var last = hops[terminal];
        if (last.IsSilent || last.LossPercent < SignificantLossPercent)
        {
            return null;
        }

        HopStatistics? candidate = null;

        for (var i = terminal; i >= 0; i--)
        {
            var hop = hops[i];

            if (hop.IsSilent)
            {
                continue;
            }

            if (hop.LossPercent >= SignificantLossPercent)
            {
                candidate = hop;
                continue;
            }

            // Дошли до хопа без потерь — значит деградация началась после него.
            break;
        }

        return candidate;
    }
}
