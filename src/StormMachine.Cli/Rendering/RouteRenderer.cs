using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ разобранного маршрута: таблица хопов, смены пути и вывод.
/// </summary>
/// <remarks>
/// Вынесено отдельно, потому что маршрут показывают из двух мест: сразу после прогона
/// и при просмотре сохранённого прогона. Разбор один и тот же, и расходиться этим двум
/// показам нельзя — иначе отчёт по журналу перестанет совпадать с тем, что оператор
/// видел своими глазами.
/// </remarks>
internal static class RouteRenderer
{
    private const int MaxChangesShown = 10;

    private static string F(double value) => Units.Number(value, MeasurementUnit.Milliseconds);

    /// <summary>
    /// Восстанавливает разбор маршрута: по сырым сэмплам, а после их удаления — по агрегатам.
    /// </summary>
    public static PathAnalysis Analyse(StoredRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.Samples.Count > 0
            ? PathAnalysis.Compute(run.Samples, run.Summary.ResolvedAddress)
            : PathAnalysis.FromSeries(run.Series, run.Summary.ResolvedAddress);
    }

    public static void Write(PathAnalysis analysis, IReadOnlyList<ProbeFact> facts)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(facts);

        if (analysis.Hops.Count == 0)
        {
            Console.WriteLine("Маршрут не построен.");
            return;
        }

        var annotations = Annotations(facts);

        Console.WriteLine();
        Console.WriteLine($"  {"хоп",3}  {"узел",-24} {"отпр",5} {"потери",7} "
                          + $"{"мин",8} {"медиана",8} {"макс",8} {"джиттер",8}  {"MOS",4}");

        foreach (var hop in analysis.Hops)
        {
            WriteHop(hop, annotations);
        }

        Console.WriteLine();
        Console.WriteLine("  MOS на транзитных хопах считается по задержке и дрожанию, без потерь:");
        Console.WriteLine("  потери на транзитном узле — это ограничение его ответов, а не потеря трафика.");

        WriteEarlyDestination(analysis);
        WriteChanges(analysis, annotations);
        WriteVerdict(analysis, annotations);
    }

    /// <summary>
    /// Объясняет цель, отвечающую с нескольких TTL.
    /// </summary>
    /// <remarks>
    /// Без пояснения такие строки читаются как «до цели девяносто процентов потерь»,
    /// хотя означают ровно обратное: часть пакетов дошла коротким путём.
    /// </remarks>
    private static void WriteEarlyDestination(PathAnalysis analysis)
    {
        if (analysis.EarlyDestinationHops.Count == 0)
        {
            return;
        }

        var shares = analysis.Hops
            .Where(h => h.IsEarlyDestination)
            .Select(h => $"{h.Hop} ({h.ShortPathPercent.ToString("0.#", CultureInfo.InvariantCulture)}%)");

        Console.WriteLine();
        Console.WriteLine($"  Цель отвечала также с хопов: {string.Join(", ", shares)}.");
        Console.WriteLine("  В скобках — доля пакетов, дошедших коротким путём. Длина пути непостоянна:");
        Console.WriteLine("  обычное дело для туннелей MPLS без переноса TTL и балансировки по каналам.");
        Console.WriteLine("  Остальные пакеты не потеряны — они дошли длинным путём, до конечной точки.");
    }

    /// <summary>
    /// Факты, которые ещё нужно показать списком после таблицы маршрута.
    /// </summary>
    /// <remarks>
    /// Категория route уже разошлась подписями под адресами хопов — повторять её
    /// общим списком значило бы напечатать три десятка строк второй раз.
    /// </remarks>
    public static IReadOnlyList<ProbeFact> RemainingFacts(IReadOnlyList<ProbeFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return [.. facts.Where(f => !string.Equals(f.Category, HopAnnotation.FactCategory, StringComparison.OrdinalIgnoreCase))];
    }

    private static void WriteHop(HopStatistics hop, IReadOnlyDictionary<string, string> annotations)
    {
        // У хопа с ранним ответом цели в колонке потерь стоит прочерк: доля пакетов,
        // ушедших длинным путём, — не потери, и цифра здесь читалась бы как авария.
        var loss = hop.IsEarlyDestination
            ? "—"
            : hop.LossPercent.ToString("0", CultureInfo.InvariantCulture) + "%";

        if (hop.IsSilent)
        {
            Console.WriteLine($"  {hop.Hop,3}  {"*",-24} {hop.Sent,5} {loss,7} "
                              + $"{"—",8} {"—",8} {"—",8} {"—",8}  {"—",4}");
            return;
        }

        var address = hop.Address ?? "*";
        var mos = double.IsNaN(hop.Voice.Mos) ? "—" : hop.Voice.Mos.ToString("0.0", CultureInfo.InvariantCulture);
        var marker = hop.IsDestination ? "→" : " ";

        Console.WriteLine($" {marker}{hop.Hop,3}  {address,-24} {hop.Sent,5} {loss,7} "
                          + $"{F(hop.Statistics.MinMs),8} {F(hop.Statistics.P50Ms),8} {F(hop.Statistics.MaxMs),8} "
                          + $"{F(hop.Statistics.JitterRfc3550Ms),8}  {mos,4}");

        if (hop.IsEarlyDestination)
        {
            Console.WriteLine("       цель коротким путём");
        }

        if (Annotation(annotations, address) is { } text)
        {
            Console.WriteLine($"       {text}");
        }

        if (hop.Addresses.Count > 1)
        {
            var others = hop.Addresses.Where(a => !string.Equals(a, address, StringComparison.Ordinal));
            Console.WriteLine($"       также отвечали: {string.Join(", ", others)}");
        }
    }

    private static void WriteChanges(PathAnalysis analysis, IReadOnlyDictionary<string, string> annotations)
    {
        if (analysis.RouteChanges.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  Смен маршрута: {analysis.RouteChanges.Count}");

        foreach (var change in analysis.RouteChanges.Take(MaxChangesShown))
        {
            var to = Annotation(annotations, change.To) is { } text ? $"{change.To} ({text})" : change.To;
            Console.WriteLine($"    хоп {change.Hop}: {change.From} → {to}");
        }

        if (analysis.RouteChanges.Count > MaxChangesShown)
        {
            Console.WriteLine($"    …и ещё {analysis.RouteChanges.Count - MaxChangesShown}");
        }
    }

    private static void WriteVerdict(PathAnalysis analysis, IReadOnlyDictionary<string, string> annotations)
    {
        if (!analysis.DestinationReached && analysis.DegradationPoint is null)
        {
            // Сказать нечего: почему цель не достигнута, объясняет факт «Итог».
            return;
        }

        Console.WriteLine();

        if (analysis.DestinationReached && !double.IsNaN(analysis.DestinationVoice.Mos))
        {
            var voice = analysis.DestinationVoice;
            Console.WriteLine($"  Качество до цели: {voice.Grade} "
                              + $"(MOS {voice.Mos.ToString("0.00", CultureInfo.InvariantCulture)}, "
                              + $"R {voice.RFactor.ToString("0.0", CultureInfo.InvariantCulture)}) — "
                              + "упрощённая E-модель ITU-T G.107");
        }

        if (analysis.DegradationPoint is { } point)
        {
            var address = point.Address ?? "?";
            var where = Annotation(annotations, address) is { } text ? $"{address} ({text})" : address;

            Console.WriteLine($"  Деградация начинается на хопе {point.Hop}: {where}, "
                              + $"потери {point.LossPercent.ToString("0.0", CultureInfo.InvariantCulture)}% "
                              + "и держатся до конца маршрута.");
        }
        else if (analysis.DestinationReached)
        {
            Console.WriteLine("  Устойчивых потерь по маршруту нет: до цели пакеты доходят.");
        }
    }

    /// <summary>Таблица «адрес → чем известен», собранная пробой в фактах категории route.</summary>
    private static Dictionary<string, string> Annotations(IReadOnlyList<ProbeFact> facts)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            if (string.Equals(fact.Category, HopAnnotation.FactCategory, StringComparison.OrdinalIgnoreCase))
            {
                map[fact.Name] = fact.Value;
            }
        }

        return map;
    }

    private static string? Annotation(IReadOnlyDictionary<string, string> annotations, string address) =>
        annotations.TryGetValue(address, out var text) && text != HopAnnotation.PrivateLabel
            ? text
            : null;
}
