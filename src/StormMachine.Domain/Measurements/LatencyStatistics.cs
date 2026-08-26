namespace StormMachine.Domain.Measurements;

/// <summary>
/// Агрегаты по серии измерений задержки.
/// </summary>
/// <remarks>
/// Считается поверх сырых сэмплов и в них не хранится: один и тот же набор данных
/// можно пересчитать другой методикой, не теряя исходник.
/// </remarks>
public sealed record LatencyStatistics
{
    public required int SampleCount { get; init; }

    public required double MinMs { get; init; }

    public required double MaxMs { get; init; }

    public required double MeanMs { get; init; }

    public required double StdDevMs { get; init; }

    public required double P50Ms { get; init; }

    public required double P95Ms { get; init; }

    public required double P99Ms { get; init; }

    /// <summary>
    /// Сглаженный джиттер по RFC 3550 §6.4.1: <c>J += (|D(i-1,i)| - J) / 16</c>.
    /// </summary>
    /// <remarks>
    /// Это не «разброс пинга» и не стандартное отклонение. Отчёт, ссылающийся на RFC 3550,
    /// имеет вес в разговоре с провайдером; отчёт с самодельной формулой — нет.
    /// </remarks>
    public required double JitterRfc3550Ms { get; init; }

    /// <summary>
    /// Вариация задержки пакетов: <c>p99 − p50</c>. Показывает хвост распределения,
    /// который среднее и стандартное отклонение скрывают.
    /// </summary>
    public double PdvMs => P99Ms - P50Ms;

    public static readonly LatencyStatistics Empty = new()
    {
        SampleCount = 0,
        MinMs = double.NaN,
        MaxMs = double.NaN,
        MeanMs = double.NaN,
        StdDevMs = double.NaN,
        P50Ms = double.NaN,
        P95Ms = double.NaN,
        P99Ms = double.NaN,
        JitterRfc3550Ms = double.NaN,
    };

    /// <summary>Считает агрегаты по успешным сэмплам. Неуспешные пропускаются.</summary>
    public static LatencyStatistics Compute(IReadOnlyList<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var values = new List<double>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].IsSuccess)
            {
                values.Add(samples[i].Value);
            }
        }

        if (values.Count == 0)
        {
            return Empty;
        }

        // Джиттер считается по ПОРЯДКУ ПРИБЫТИЯ, до сортировки:
        // формула RFC 3550 опирается на разницу соседних измерений.
        var jitter = ComputeRfc3550Jitter(values);

        var ordered = values.ToArray();
        Array.Sort(ordered);

        double sum = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            sum += ordered[i];
        }

        var mean = sum / ordered.Length;

        double sumSquares = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var d = ordered[i] - mean;
            sumSquares += d * d;
        }

        return new LatencyStatistics
        {
            SampleCount = ordered.Length,
            MinMs = ordered[0],
            MaxMs = ordered[^1],
            MeanMs = mean,
            StdDevMs = Math.Sqrt(sumSquares / ordered.Length),
            P50Ms = Percentile(ordered, 0.50),
            P95Ms = Percentile(ordered, 0.95),
            P99Ms = Percentile(ordered, 0.99),
            JitterRfc3550Ms = jitter,
        };
    }

    /// <summary>Перцентиль по методу ближайшего ранга. Массив должен быть отсортирован.</summary>
    public static double Percentile(double[] sortedValues, double quantile)
    {
        ArgumentNullException.ThrowIfNull(sortedValues);

        if (sortedValues.Length == 0)
        {
            return double.NaN;
        }

        var rank = (int)Math.Ceiling(quantile * sortedValues.Length);
        var index = Math.Clamp(rank - 1, 0, sortedValues.Length - 1);
        return sortedValues[index];
    }

    private static double ComputeRfc3550Jitter(List<double> valuesInArrivalOrder)
    {
        double jitter = 0;

        for (var i = 1; i < valuesInArrivalOrder.Count; i++)
        {
            var delta = Math.Abs(valuesInArrivalOrder[i] - valuesInArrivalOrder[i - 1]);
            jitter += (delta - jitter) / 16.0;
        }

        return jitter;
    }
}
