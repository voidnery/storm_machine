using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки разбора маршрута.
/// </summary>
/// <remarks>
/// Главное здесь — правило определения точки деградации. Самая частая ошибка чтения
/// traceroute состоит в том, что потери на промежуточном хопе принимают за поломку канала,
/// хотя это ограничение частоты ответов самого узла. Инструмент, повторяющий эту ошибку,
/// хуже отсутствия инструмента: он уводит разбор в неверную сторону с видом уверенности.
/// </remarks>
public sealed class PathAnalysisTests
{
    private static Sample Ok(int sequence, int hop, double value, string by) => new()
    {
        Sequence = sequence,
        TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
        Value = value,
        Status = SampleStatus.Success,
        Label = by,
        Group = hop,
        RespondedBy = by,
        Ttl = hop,
    };

    private static Sample Lost(int sequence, int hop) =>
        Sample.Failed(sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), SampleStatus.Timeout)
            with { Group = hop, Ttl = hop };

    private static readonly int[] HealthyHopNumbers = [1, 2, 3];

    private static readonly string[] BalancedAddresses = ["203.0.113.1", "203.0.113.9"];

    private static readonly int[] SecondHopOnly = [2];

    /// <summary>Ровный маршрут: три хопа по два пакета, всё дошло.</summary>
    private static List<Sample> HealthyPath() =>
    [
        Ok(0, 1, 1.0, "10.0.0.1"), Ok(1, 1, 1.2, "10.0.0.1"),
        Ok(2, 2, 5.0, "203.0.113.1"), Ok(3, 2, 5.4, "203.0.113.1"),
        Ok(4, 3, 9.0, "198.51.100.7"), Ok(5, 3, 9.2, "198.51.100.7"),
    ];

    [Fact]
    public void GroupsSamplesByHop()
    {
        var analysis = PathAnalysis.Compute(HealthyPath(), "198.51.100.7");

        Assert.Equal(3, analysis.Hops.Count);
        Assert.Equal(HealthyHopNumbers, analysis.Hops.Select(h => h.Hop));
        Assert.All(analysis.Hops, hop => Assert.Equal(2, hop.Sent));
        Assert.Equal("198.51.100.7", analysis.Hops[^1].Address);
    }

    [Fact]
    public void MarksDestinationAndReportsReached()
    {
        var analysis = PathAnalysis.Compute(HealthyPath(), "198.51.100.7");

        Assert.True(analysis.DestinationReached);
        Assert.True(analysis.Hops[^1].IsDestination);
        Assert.False(analysis.Hops[0].IsDestination);
    }

    [Fact]
    public void SilentHop_IsNotCountedAsDegradation()
    {
        // Классический случай: середина маршрута не отвечает на ICMP, но трафик идёт.
        List<Sample> samples =
        [
            Ok(0, 1, 1.0, "10.0.0.1"),
            Lost(1, 2), Lost(2, 2),
            Ok(3, 3, 9.0, "198.51.100.7"),
        ];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.Equal(1, analysis.SilentHops);
        Assert.True(analysis.Hops[1].IsSilent);
        Assert.Null(analysis.DegradationPoint);
        Assert.True(analysis.DestinationReached);
    }

    [Fact]
    public void TransitLoss_ThatDoesNotReachDestination_IsIgnored()
    {
        // Хоп 2 теряет половину пакетов, но до цели доходит всё: это ограничение
        // частоты ответов узла, а не потеря транзитного трафика.
        List<Sample> samples =
        [
            Ok(0, 1, 1.0, "10.0.0.1"), Ok(1, 1, 1.1, "10.0.0.1"),
            Ok(2, 2, 5.0, "203.0.113.1"), Lost(3, 2),
            Ok(4, 3, 9.0, "198.51.100.7"), Ok(5, 3, 9.1, "198.51.100.7"),
        ];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.Equal(50, analysis.Hops[1].LossPercent);
        Assert.Null(analysis.DegradationPoint);
    }

    [Fact]
    public void SustainedLoss_IsAttributedToTheHopWhereItStarts()
    {
        // Потери начинаются на хопе 2 и держатся до цели — вот это уже деградация.
        List<Sample> samples =
        [
            Ok(0, 1, 1.0, "10.0.0.1"), Ok(1, 1, 1.1, "10.0.0.1"),
            Ok(2, 2, 5.0, "203.0.113.1"), Lost(3, 2),
            Ok(4, 3, 9.0, "198.51.100.7"), Lost(5, 3),
        ];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.NotNull(analysis.DegradationPoint);
        Assert.Equal(2, analysis.DegradationPoint.Hop);
        Assert.Equal("203.0.113.1", analysis.DegradationPoint.Address);
    }

    [Fact]
    public void SustainedLoss_SkipsSilentHopsWhenWalkingBack()
    {
        // Между началом потерь и целью стоит молчащий хоп. Он ничего не сообщает
        // о судьбе транзита, поэтому поиск должен идти сквозь него, а не останавливаться.
        List<Sample> samples =
        [
            Ok(0, 1, 1.0, "10.0.0.1"), Ok(1, 1, 1.1, "10.0.0.1"),
            Ok(2, 2, 5.0, "203.0.113.1"), Lost(3, 2),
            Lost(4, 3), Lost(5, 3),
            Ok(6, 4, 9.0, "198.51.100.7"), Lost(7, 4),
        ];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.NotNull(analysis.DegradationPoint);
        Assert.Equal(2, analysis.DegradationPoint.Hop);
    }

    [Fact]
    public void UnreachableDestination_HasNoDegradationPoint()
    {
        // Цель не ответила ни разу: сказать, где начались потери, не по чему —
        // последний хоп не является конечной точкой маршрута.
        List<Sample> samples = [Ok(0, 1, 1.0, "10.0.0.1"), Lost(1, 2), Lost(2, 3)];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.False(analysis.DestinationReached);
        Assert.Null(analysis.DegradationPoint);
    }

    [Fact]
    public void RouteChange_IsRecordedWithBothAddresses()
    {
        List<Sample> samples =
        [
            Ok(0, 1, 1.0, "10.0.0.1"),
            Ok(1, 2, 5.0, "203.0.113.1"),
            Ok(2, 2, 5.5, "203.0.113.9"),
            Ok(3, 2, 5.6, "203.0.113.9"),
        ];

        var analysis = PathAnalysis.Compute(samples);

        var change = Assert.Single(analysis.RouteChanges);
        Assert.Equal(2, change.Hop);
        Assert.Equal("203.0.113.1", change.From);
        Assert.Equal("203.0.113.9", change.To);
        Assert.Equal(BalancedAddresses, analysis.Hops[1].Addresses);
    }

    [Fact]
    public void SilentAnswers_DoNotCountAsRouteChanges()
    {
        // Пропущенный пакет между двумя ответами одного и того же узла — не смена пути.
        List<Sample> samples =
        [
            Ok(0, 1, 1.0, "10.0.0.1"),
            Lost(1, 1),
            Ok(2, 1, 1.1, "10.0.0.1"),
        ];

        var analysis = PathAnalysis.Compute(samples);

        Assert.Empty(analysis.RouteChanges);
    }

    [Fact]
    public void TransitHopVoice_IgnoresLoss()
    {
        // Иначе транзитный узел с ограничением ответов получал бы «непригодно»
        // на канале, по которому голос идёт прекрасно.
        List<Sample> samples =
        [
            Ok(0, 1, 5.0, "203.0.113.1"), Lost(1, 1), Lost(2, 1), Lost(3, 1),
            Ok(4, 2, 6.0, "198.51.100.7"), Ok(5, 2, 6.1, "198.51.100.7"),
        ];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.Equal(75, analysis.Hops[0].LossPercent);
        Assert.True(analysis.Hops[0].Voice.IsAcceptableForVoice,
            "Потери транзитного узла не должны опускать его оценку.");
    }

    [Fact]
    public void DestinationVoice_CountsLoss()
    {
        List<Sample> samples =
        [
            Ok(0, 1, 5.0, "203.0.113.1"),
            Ok(1, 2, 6.0, "198.51.100.7"), Lost(2, 2), Lost(3, 2), Lost(4, 2),
        ];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.False(analysis.DestinationVoice.IsAcceptableForVoice,
            "На конечном узле потери настоящие и обязаны учитываться.");
    }

    [Fact]
    public void SamplesWithoutHop_AreIgnored()
    {
        var stray = Sample.Failed(0, DateTimeOffset.UnixEpoch, SampleStatus.Error);

        var analysis = PathAnalysis.Compute([stray, Ok(1, 1, 1.0, "10.0.0.1")]);

        var hop = Assert.Single(analysis.Hops);
        Assert.Equal(1, hop.Hop);
    }

    /// <summary>
    /// Цель, отвечающая с нескольких TTL, — это переменная длина пути, а не вторая цель.
    /// </summary>
    /// <remarks>
    /// Наблюдалось на непрерывном MTR к 8.8.8.8: несколько процентов пакетов доходили
    /// до цели с TTL 15, 17 и 19 при обычной длине маршрута в 21 хоп. Метка в полезной
    /// нагрузке подтвердила, что это наши собственные пакеты, а не чужие ответы, —
    /// обычное поведение туннелей MPLS без переноса TTL.
    /// <para>
    /// Показать такой хоп конечной точкой значило бы объявить «до цели 97% потерь»
    /// там, где потерь нет вовсе.
    /// </para>
    /// </remarks>
    [Fact]
    public void DestinationAnsweringAtSeveralHops_KeepsOnlyTheLastAsTerminal()
    {
        List<Sample> samples =
        [
            Ok(0, 1, 1.0, "10.0.0.1"),
            // Хоп 2: цель ответила один раз из четырёх — короткий путь.
            Ok(1, 2, 20.0, "198.51.100.7"), Lost(2, 2), Lost(3, 2), Lost(4, 2),
            Ok(5, 3, 12.0, "198.51.100.7"), Ok(6, 3, 12.1, "198.51.100.7"),
        ];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.False(analysis.Hops[1].IsDestination, "Ранний ответ цели не делает хоп конечной точкой.");
        Assert.True(analysis.Hops[2].IsDestination);
        Assert.Equal(SecondHopOnly, analysis.EarlyDestinationHops);
        Assert.True(analysis.Hops[1].IsEarlyDestination);
        Assert.False(analysis.Hops[2].IsEarlyDestination, "Конечная точка ранним ответом не считается.");

        // Доля коротким путём — величина, обратная «потерям» этой строки: один пакет
        // из четырёх дошёл раньше, три ушли длинным путём и тоже дошли.
        Assert.Equal(25, analysis.Hops[1].ShortPathPercent, 6);

        // Потери раннего хопа — доля пакетов, ушедших длинным путём.
        // В оценку качества они входить не должны.
        Assert.True(analysis.Hops[1].Voice.IsAcceptableForVoice);
        Assert.True(analysis.DestinationVoice.IsAcceptableForVoice);
        Assert.Null(analysis.DegradationPoint);
    }

    [Fact]
    public void DegradationPoint_IsMeasuredFromTheTerminalHop()
    {
        // За конечной точкой стоит хоп, отвечавший раньше при другой длине пути.
        // Отсчёт «от последней строки таблицы» принял бы его за конец маршрута.
        List<Sample> samples =
        [
            Ok(0, 1, 1.0, "10.0.0.1"), Ok(1, 1, 1.1, "10.0.0.1"),
            Ok(2, 2, 5.0, "203.0.113.1"), Lost(3, 2),
            Ok(4, 3, 9.0, "198.51.100.7"), Lost(5, 3),
            Lost(6, 4), Lost(7, 4),
        ];

        var analysis = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.NotNull(analysis.DegradationPoint);
        Assert.Equal(2, analysis.DegradationPoint.Hop);
    }

    [Fact]
    public void SingleDestinationHop_LeavesEarlyListEmpty()
    {
        var analysis = PathAnalysis.Compute(HealthyPath(), "198.51.100.7");

        Assert.Empty(analysis.EarlyDestinationHops);
    }

    [Fact]
    public void FromSeries_RestoresHopsAndDegradationPoint()
    {
        // Сырые сэмплы удалены политикой хранения — разбор должен восстанавливаться
        // из агрегатов, иначе отчёт по старому прогону потеряет вывод.
        var samples = new List<Sample>
        {
            Ok(0, 1, 1.0, "10.0.0.1"), Ok(1, 1, 1.1, "10.0.0.1"),
            Ok(2, 2, 5.0, "203.0.113.1"), Lost(3, 2),
            Ok(4, 3, 9.0, "198.51.100.7"), Lost(5, 3),
        };

        var series = SeriesBreakdown.Compute(ProbeResultShape.PathTrace, samples);
        var restored = PathAnalysis.FromSeries(series, "198.51.100.7");
        var original = PathAnalysis.Compute(samples, "198.51.100.7");

        Assert.Equal(original.Hops.Count, restored.Hops.Count);
        Assert.Equal(original.DestinationReached, restored.DestinationReached);
        Assert.Equal(original.DegradationPoint?.Hop, restored.DegradationPoint?.Hop);
        Assert.Equal(original.SilentHops, restored.SilentHops);

        // Историю адресов агрегаты не хранят — смены маршрута восстановить нельзя,
        // и показывать ноль там, где их не по чему считать, было бы враньём.
        Assert.Empty(restored.RouteChanges);
    }

    [Fact]
    public void FromSeries_IgnoresWholeRunRow()
    {
        var series = new List<SeriesStatistics>
        {
            SeriesBreakdown.WholeRun([Ok(0, 1, 1.0, "10.0.0.1")]),
            new()
            {
                Key = "hop:1",
                Label = "10.0.0.1",
                SentCount = 1,
                SuccessCount = 1,
                Statistics = LatencyStatistics.Compute([Ok(0, 1, 1.0, "10.0.0.1")]),
            },
        };

        var analysis = PathAnalysis.FromSeries(series);

        var hop = Assert.Single(analysis.Hops);
        Assert.Equal(1, hop.Hop);
    }

    [Fact]
    public void EmptyInput_ProducesEmptyAnalysis()
    {
        var analysis = PathAnalysis.Compute([]);

        Assert.Empty(analysis.Hops);
        Assert.False(analysis.DestinationReached);
        Assert.Null(analysis.DegradationPoint);
        Assert.True(double.IsNaN(analysis.DestinationVoice.Mos));
    }
}
