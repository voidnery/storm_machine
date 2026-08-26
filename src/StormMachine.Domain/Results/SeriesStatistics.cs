using StormMachine.Domain.Measurements;

namespace StormMachine.Domain.Results;

/// <summary>
/// Агрегаты по одному ряду внутри результата.
/// </summary>
/// <remarks>
/// Ряд — это фаза HTTP, резолвер DNS, хоп traceroute или весь прогон целиком для
/// скалярных проб. Единая форма для всех четырёх видов результата: именно она позволяет
/// хранить историю в одной таблице, не заводя по таблице на каждый вид пробы.
/// </remarks>
public sealed record SeriesStatistics
{
    /// <summary>
    /// Ключ ряда: пусто — весь прогон, иначе имя фазы, адрес резолвера или <c>hop:N</c>.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Человекочитаемое имя ряда для показа.</summary>
    public required string Label { get; init; }

    public required int SentCount { get; init; }

    public required int SuccessCount { get; init; }

    public required LatencyStatistics Statistics { get; init; }

    public int LostCount => SentCount - SuccessCount;

    public double LossPercent => SentCount == 0 ? 0 : LostCount * 100.0 / SentCount;
}

/// <summary>
/// Раскладка результата на ряды по объявленной форме.
/// </summary>
/// <remarks>
/// Существует ради хранения и отчётов: сырые сэмплы со временем удаляются политикой
/// хранения, а агрегаты остаются. Считать их нужно один раз при записи — потом
/// пересчитывать будет уже не из чего.
/// <para>
/// Форма берётся из объявления пробы. Это второе место после показа, где объявление
/// формы окупается: одна и та же функция раскладывает водопад HTTP, сравнение резолверов
/// и матрицу хопов, ничего не угадывая.
/// </para>
/// </remarks>
public static class SeriesBreakdown
{
    /// <summary>Ключ ряда, означающего весь прогон целиком.</summary>
    public const string WholeRunKey = "";

    public static IReadOnlyList<SeriesStatistics> Compute(ProbeResultShape shape, IReadOnlyList<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return shape switch
        {
            ProbeResultShape.PhasedTiming => ByLabel(samples, label => PhaseTitle(label)),
            ProbeResultShape.ComparedSeries => ByLabel(samples, label => label),
            ProbeResultShape.PathTrace => ByHop(samples),
            _ => [WholeRun(samples)],
        };
    }

    /// <summary>
    /// Агрегат по всему прогону.
    /// </summary>
    /// <remarks>
    /// Считается всегда, даже для составных форм: список прогонов должен показывать
    /// одну цифру на строку, не разворачивая раскладку.
    /// </remarks>
    public static SeriesStatistics WholeRun(IReadOnlyList<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return new SeriesStatistics
        {
            Key = WholeRunKey,
            Label = "весь прогон",
            SentCount = samples.Count,
            SuccessCount = CountSuccessful(samples),
            Statistics = LatencyStatistics.Compute(samples),
        };
    }

    private static List<SeriesStatistics> ByLabel(IReadOnlyList<Sample> samples, Func<string, string> title)
    {
        var order = new List<string>();
        var buckets = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);

        foreach (var sample in samples)
        {
            var key = sample.Label ?? "—";

            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                buckets[key] = bucket;
                order.Add(key);
            }

            bucket.Add(sample);
        }

        var result = new List<SeriesStatistics>(order.Count);

        foreach (var key in order)
        {
            var bucket = buckets[key];
            result.Add(new SeriesStatistics
            {
                Key = key,
                Label = title(key),
                SentCount = bucket.Count,
                SuccessCount = CountSuccessful(bucket),
                Statistics = LatencyStatistics.Compute(bucket),
            });
        }

        return result;
    }

    private static List<SeriesStatistics> ByHop(IReadOnlyList<Sample> samples)
    {
        var buckets = new SortedDictionary<int, List<Sample>>();

        foreach (var sample in samples)
        {
            var hop = sample.Group ?? 0;

            if (!buckets.TryGetValue(hop, out var bucket))
            {
                bucket = [];
                buckets[hop] = bucket;
            }

            bucket.Add(sample);
        }

        var result = new List<SeriesStatistics>(buckets.Count);

        foreach (var (hop, bucket) in buckets)
        {
            // Адрес узла берётся из ответов, а не из ключа: молчащий хоп даст пустую
            // строку, и это честнее выдуманного имени.
            var responder = bucket
                .Select(s => s.RespondedBy)
                .FirstOrDefault(r => !string.IsNullOrEmpty(r));

            result.Add(new SeriesStatistics
            {
                Key = $"hop:{hop}",
                Label = responder ?? "*",
                SentCount = bucket.Count,
                SuccessCount = CountSuccessful(bucket),
                Statistics = LatencyStatistics.Compute(bucket),
            });
        }

        return result;
    }

    private static int CountSuccessful(IReadOnlyList<Sample> samples)
    {
        var count = 0;

        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].IsSuccess)
            {
                count++;
            }
        }

        return count;
    }

    private static string PhaseTitle(string label) => label switch
    {
        "dns" => "DNS",
        "connect" => "TCP",
        "tls" => "TLS",
        "ttfb" => "первый байт",
        "download" => "скачивание",
        _ => label,
    };
}
