using StormMachine.Domain.Measurements;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки агрегатов задержки.
/// </summary>
/// <remarks>
/// Джиттер здесь — не «разброс пинга» и не стандартное отклонение, а формула
/// RFC 3550 §6.4.1. Отчёт со ссылкой на стандарт имеет вес в разговоре с провайдером,
/// отчёт с самодельной формулой — нет. Поэтому формула зафиксирована тестами.
/// </remarks>
public sealed class LatencyStatisticsTests
{
    private static Sample Ok(int sequence, double value) =>
        Sample.Ok(sequence, DateTimeOffset.UnixEpoch, value);

    private static Sample Lost(int sequence) =>
        Sample.Failed(sequence, DateTimeOffset.UnixEpoch, SampleStatus.Timeout);

    [Fact]
    public void Empty_ReturnsNaNWithoutThrowing()
    {
        var stats = LatencyStatistics.Compute([]);

        Assert.Equal(0, stats.SampleCount);
        Assert.True(double.IsNaN(stats.MeanMs));
    }

    [Fact]
    public void AllLost_ReturnsEmpty()
    {
        var stats = LatencyStatistics.Compute([Lost(0), Lost(1), Lost(2)]);

        Assert.Equal(0, stats.SampleCount);
    }

    [Fact]
    public void LostSamples_AreExcludedFromStatistics()
    {
        var stats = LatencyStatistics.Compute([Ok(0, 1.0), Lost(1), Ok(2, 3.0)]);

        Assert.Equal(2, stats.SampleCount);
        Assert.Equal(2.0, stats.MeanMs, 6);
        Assert.Equal(1.0, stats.MinMs, 6);
        Assert.Equal(3.0, stats.MaxMs, 6);
    }

    [Fact]
    public void ConstantSeries_HasZeroJitterAndZeroSpread()
    {
        var samples = Enumerable.Range(0, 50).Select(i => Ok(i, 5.0)).ToList();

        var stats = LatencyStatistics.Compute(samples);

        Assert.Equal(5.0, stats.MeanMs, 6);
        Assert.Equal(0.0, stats.StdDevMs, 6);
        Assert.Equal(0.0, stats.JitterRfc3550Ms, 6);
        Assert.Equal(0.0, stats.PdvMs, 6);
    }

    [Fact]
    public void Jitter_FollowsRfc3550Formula()
    {
        // J += (|D(i-1,i)| - J) / 16, начиная с J = 0.
        // Серия 1, 2, 1, 2: каждая разница равна 1.
        var samples = new[] { Ok(0, 1.0), Ok(1, 2.0), Ok(2, 1.0), Ok(3, 2.0) };

        double expected = 0;
        foreach (var delta in new[] { 1.0, 1.0, 1.0 })
        {
            expected += (delta - expected) / 16.0;
        }

        var stats = LatencyStatistics.Compute(samples);

        Assert.Equal(expected, stats.JitterRfc3550Ms, 9);
    }

    [Fact]
    public void Jitter_IsNotStandardDeviation()
    {
        // Частая ошибка — подменить джиттер стандартным отклонением.
        // Формула RFC 3550 сглаживает и даёт заметно меньшее значение.
        var samples = new[] { Ok(0, 1.0), Ok(1, 10.0), Ok(2, 1.0), Ok(3, 10.0), Ok(4, 1.0) };

        var stats = LatencyStatistics.Compute(samples);

        Assert.True(
            stats.JitterRfc3550Ms < stats.StdDevMs,
            $"Джиттер {stats.JitterRfc3550Ms:0.###} не должен совпадать со стандартным отклонением {stats.StdDevMs:0.###}");
    }

    [Fact]
    public void Jitter_UsesArrivalOrderNotSortedOrder()
    {
        // Формула опирается на разницу СОСЕДНИХ по времени измерений.
        // Если считать по отсортированному массиву, результат будет другим —
        // и это была бы тихая ошибка, незаметная на глаз.
        var chaotic = new[] { Ok(0, 1.0), Ok(1, 9.0), Ok(2, 2.0), Ok(3, 8.0) };
        var ordered = new[] { Ok(0, 1.0), Ok(1, 2.0), Ok(2, 8.0), Ok(3, 9.0) };

        var chaoticStats = LatencyStatistics.Compute(chaotic);
        var orderedStats = LatencyStatistics.Compute(ordered);

        Assert.Equal(chaoticStats.MeanMs, orderedStats.MeanMs, 9);
        Assert.NotEqual(chaoticStats.JitterRfc3550Ms, orderedStats.JitterRfc3550Ms, 9);
        Assert.True(chaoticStats.JitterRfc3550Ms > orderedStats.JitterRfc3550Ms);
    }

    [Theory]
    [InlineData(0.50, 50.0)]
    [InlineData(0.95, 95.0)]
    [InlineData(0.99, 99.0)]
    [InlineData(1.00, 100.0)]
    public void Percentile_UsesNearestRank(double quantile, double expected)
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        Assert.Equal(expected, LatencyStatistics.Percentile(sorted, quantile), 6);
    }

    [Fact]
    public void Percentiles_AreMonotonic()
    {
        var samples = Enumerable.Range(0, 500)
            .Select(i => Ok(i, 1.0 + ((i * 37 % 500) / 100.0)))
            .ToList();

        var stats = LatencyStatistics.Compute(samples);

        Assert.True(stats.MinMs <= stats.P50Ms);
        Assert.True(stats.P50Ms <= stats.P95Ms);
        Assert.True(stats.P95Ms <= stats.P99Ms);
        Assert.True(stats.P99Ms <= stats.MaxMs);
    }

    [Fact]
    public void Pdv_ShowsTailHiddenByMeanAndStdDev()
    {
        // 98 ровных измерений и два выброса: медиана не шелохнулась,
        // а PDV обнажает хвост — ради этого метрика и нужна.
        var samples = Enumerable.Range(0, 98).Select(i => Ok(i, 1.0)).ToList();
        samples.Add(Ok(98, 100.0));
        samples.Add(Ok(99, 100.0));

        var stats = LatencyStatistics.Compute(samples);

        Assert.Equal(1.0, stats.P50Ms, 6);
        Assert.True(stats.PdvMs > 90, $"PDV {stats.PdvMs:0.###} должен обнажать выброс");
    }

    [Fact]
    public void P99_DoesNotCaptureSingleOutlierInHundredSamples()
    {
        // Свойство перцентиля по ближайшему рангу, о котором легко забыть при чтении
        // отчёта: при 100 измерениях p99 — это 99-е значение по возрастанию, и ровно
        // один выброс в него не попадает. Чтобы хвост стал виден, нужна либо более
        // длинная серия, либо взгляд на max.
        var samples = Enumerable.Range(0, 99).Select(i => Ok(i, 1.0)).ToList();
        samples.Add(Ok(99, 100.0));

        var stats = LatencyStatistics.Compute(samples);

        Assert.Equal(1.0, stats.P99Ms, 6);
        Assert.Equal(0.0, stats.PdvMs, 6);
        Assert.Equal(100.0, stats.MaxMs, 6);
    }
}
