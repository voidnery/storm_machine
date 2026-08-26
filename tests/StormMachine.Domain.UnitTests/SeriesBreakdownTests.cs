using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки раскладки результата на ряды.
/// </summary>
/// <remarks>
/// Здесь окупается объявление формы результата: одна функция раскладывает водопад HTTP,
/// сравнение резолверов и матрицу хопов, ничего не угадывая по содержимому сэмплов.
/// Тесты закрепляют, что для каждой формы получается именно то разбиение, по которому
/// потом строятся хранение и отчёты.
/// </remarks>
public sealed class SeriesBreakdownTests
{
    private static Sample Ok(int sequence, double value, string? label = null, int? group = null, string? by = null) => new()
    {
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UnixEpoch,
        Value = value,
        Status = SampleStatus.Success,
        Label = label,
        Group = group,
        RespondedBy = by,
    };

    private static Sample Lost(int sequence, string? label = null, int? group = null) =>
        Sample.Failed(sequence, DateTimeOffset.UnixEpoch, SampleStatus.Timeout) with { Label = label, Group = group };

    [Fact]
    public void ScalarSeries_ProducesSingleWholeRunSeries()
    {
        var series = SeriesBreakdown.Compute(
            ProbeResultShape.ScalarSeries,
            [Ok(0, 1.0), Ok(1, 2.0), Lost(2)]);

        var single = Assert.Single(series);
        Assert.Equal(SeriesBreakdown.WholeRunKey, single.Key);
        Assert.Equal(3, single.SentCount);
        Assert.Equal(2, single.SuccessCount);
        Assert.Equal(1, single.LostCount);
    }

    [Fact]
    public void PhasedTiming_ProducesSeriesPerPhaseInOrderOfAppearance()
    {
        // Порядок фаз — это порядок событий во времени, а не алфавит:
        // водопад, отсортированный по алфавиту, перестаёт быть водопадом.
        var series = SeriesBreakdown.Compute(
            ProbeResultShape.PhasedTiming,
            [
                Ok(0, 10, "dns", 0),
                Ok(0, 40, "connect", 0),
                Ok(0, 60, "tls", 0),
                Ok(0, 100, "ttfb", 0),
            ]);

        Assert.Equal(4, series.Count);
        Assert.Equal(["dns", "connect", "tls", "ttfb"], series.Select(s => s.Key));
        Assert.Equal(["DNS", "TCP", "TLS", "первый байт"], series.Select(s => s.Label));
    }

    [Fact]
    public void PhasedTiming_AggregatesRepeatedAttempts()
    {
        var series = SeriesBreakdown.Compute(
            ProbeResultShape.PhasedTiming,
            [
                Ok(0, 10, "dns", 0),
                Ok(0, 40, "connect", 0),
                Ok(1, 20, "dns", 1),
                Ok(1, 50, "connect", 1),
            ]);

        Assert.Equal(2, series.Count);

        var dns = series.First(s => s.Key == "dns");
        Assert.Equal(2, dns.SentCount);
        Assert.Equal(10, dns.Statistics.MinMs, 6);
        Assert.Equal(20, dns.Statistics.MaxMs, 6);
    }

    [Fact]
    public void ComparedSeries_KeepsResolverNamesAsIs()
    {
        var series = SeriesBreakdown.Compute(
            ProbeResultShape.ComparedSeries,
            [
                Ok(0, 1.0, "192.168.0.1", 0),
                Ok(1, 1.2, "192.168.0.1", 0),
                Ok(2, 40.0, "8.8.8.8", 1),
            ]);

        Assert.Equal(2, series.Count);
        Assert.Equal("192.168.0.1", series[0].Label);
        Assert.Equal(2, series[0].SentCount);
        Assert.Equal("8.8.8.8", series[1].Label);
    }

    [Fact]
    public void PathTrace_GroupsByHopAndSortsByHopNumber()
    {
        // Сэмплы приходят вперемешку по попыткам, но хопы обязаны идти по порядку:
        // маршрут, показанный не по порядку, читать нельзя.
        var series = SeriesBreakdown.Compute(
            ProbeResultShape.PathTrace,
            [
                Ok(0, 1.0, group: 2, by: "10.0.0.2"),
                Ok(1, 0.5, group: 1, by: "10.0.0.1"),
                Ok(2, 1.1, group: 2, by: "10.0.0.2"),
            ]);

        Assert.Equal(["hop:1", "hop:2"], series.Select(s => s.Key));
        Assert.Equal("10.0.0.1", series[0].Label);
        Assert.Equal(2, series[1].SentCount);
    }

    [Fact]
    public void PathTrace_SilentHopIsMarkedWithStar()
    {
        var series = SeriesBreakdown.Compute(
            ProbeResultShape.PathTrace,
            [Lost(0, group: 1), Lost(1, group: 1)]);

        var hop = Assert.Single(series);
        Assert.Equal("*", hop.Label);
        Assert.Equal(0, hop.SuccessCount);
        Assert.Equal(100, hop.LossPercent, 6);
    }

    [Fact]
    public void WholeRun_IsComputedForAnyShape()
    {
        var samples = new[] { Ok(0, 10, "dns", 0), Ok(0, 30, "connect", 0) };

        var whole = SeriesBreakdown.WholeRun(samples);

        Assert.Equal(SeriesBreakdown.WholeRunKey, whole.Key);
        Assert.Equal(2, whole.SentCount);
        Assert.Equal(10, whole.Statistics.MinMs, 6);
        Assert.Equal(30, whole.Statistics.MaxMs, 6);
    }

    [Fact]
    public void EmptySamples_ProduceEmptyStatisticsWithoutThrowing()
    {
        foreach (var shape in Enum.GetValues<ProbeResultShape>())
        {
            var series = SeriesBreakdown.Compute(shape, []);

            Assert.All(series, s => Assert.Equal(0, s.SentCount));
        }
    }

    [Fact]
    public void SamplesWithoutLabel_FallBackToPlaceholder()
    {
        // Форма объявлена фазовой, а метки нет — данные всё равно должны разложиться,
        // а не потеряться. Инструмент диагностики не имеет права молча терять измерения.
        var series = SeriesBreakdown.Compute(ProbeResultShape.PhasedTiming, [Ok(0, 1.0)]);

        var single = Assert.Single(series);
        Assert.Equal("—", single.Key);
        Assert.Equal(1, single.SentCount);
    }
}
