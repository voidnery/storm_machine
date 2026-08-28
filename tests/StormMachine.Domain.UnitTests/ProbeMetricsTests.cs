using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки чтения метрик результата.
/// </summary>
/// <remarks>
/// Главная из них — про водопад. Первая версия считала перцентили по всем сэмплам подряд,
/// и на HTTP это давало число, которому не соответствовало ничего происходившего: один
/// запрос даёт пять длительностей, и «p95 по всем пяти» смешивает разрешение имени
/// со скачиванием. Тест закрепляет, что целым событием считается сумма фаз в пределах
/// попытки, а не выборка из фаз.
/// </remarks>
public sealed class ProbeMetricsTests
{
    private static readonly MeasurementContext TestContext = new()
    {
        InterfaceName = "test",
        AdapterKind = AdapterKind.Physical,
        CalibrationBaselineMs = 0,
        ProductVersion = "0.1.0",
        Methodology = Methodology.IcmpEcho,
        StartedUtc = DateTimeOffset.UnixEpoch,
    };

    private static Sample Phase(int attempt, string label, double value) => new()
    {
        Sequence = attempt,
        TimestampUtc = DateTimeOffset.UnixEpoch,
        Value = value,
        Status = SampleStatus.Success,
        Label = label,
        Group = attempt,
    };

    private static Sample FailedPhase(int attempt, string label) =>
        Sample.Failed(attempt, DateTimeOffset.UnixEpoch, SampleStatus.Timeout) with { Label = label, Group = attempt };

    private static ProbeResult Result(IReadOnlyList<Sample> samples, IReadOnlyList<ProbeFact>? facts = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = ProbeKind.Http,
        Target = Target.Parse("example.com"),
        Context = TestContext,
        Unit = MeasurementUnit.Milliseconds,
        Samples = samples,
        Facts = facts ?? [],
        CompletedUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void PhasedTiming_WholeEventIsSumOfPhases_NotSampleOfThem()
    {
        // Две попытки по три фазы. Целое событие — 60 мс и 60 мс, а не выборка
        // из шести значений от 5 до 40.
        var result = Result(
        [
            Phase(0, "dns", 5), Phase(0, "connect", 15), Phase(0, "tls", 40),
            Phase(1, "dns", 5), Phase(1, "connect", 15), Phase(1, "tls", 40),
        ]);

        var metrics = ProbeMetrics.Read(result, ProbeResultShape.PhasedTiming);

        Assert.Equal(60, metrics["p50"], 3);
        Assert.Equal(60, metrics["max"], 3);

        // Отправлено две попытки, а не шесть длительностей: порог «loss < 1»
        // спрашивает, сколько запросов не прошло.
        Assert.Equal(2, metrics["sent"]);
        Assert.Equal(2, metrics["received"]);
        Assert.Equal(0, metrics["loss"]);
    }

    [Fact]
    public void PhasedTiming_ExposesEachPhaseSeparately()
    {
        var result = Result([Phase(0, "dns", 5), Phase(0, "connect", 15), Phase(0, "ttfb", 200)]);

        var metrics = ProbeMetrics.Read(result, ProbeResultShape.PhasedTiming);

        // Порог на фазу «первый байт» спрашивает про сервер, а не про канал.
        Assert.Equal(200, metrics["p50@ttfb"], 3);
        Assert.Equal(5, metrics["p50@dns"], 3);
    }

    [Fact]
    public void PhasedTiming_FailedPhaseFailsWholeAttempt()
    {
        // Соединение, оборвавшееся на рукопожатии, не является наполовину успешным.
        var result = Result(
        [
            Phase(0, "dns", 5), Phase(0, "connect", 15), FailedPhase(0, "tls"),
            Phase(1, "dns", 5), Phase(1, "connect", 15), Phase(1, "tls", 40),
        ]);

        var metrics = ProbeMetrics.Read(result, ProbeResultShape.PhasedTiming);

        Assert.Equal(2, metrics["sent"]);
        Assert.Equal(1, metrics["received"]);
        Assert.Equal(50, metrics["loss"], 3);
    }

    [Fact]
    public void ScalarSeries_KeepsPlainAggregates()
    {
        var result = Result(
        [
            new Sample { Sequence = 0, TimestampUtc = DateTimeOffset.UnixEpoch, Value = 10, Status = SampleStatus.Success },
            new Sample { Sequence = 1, TimestampUtc = DateTimeOffset.UnixEpoch, Value = 20, Status = SampleStatus.Success },
        ]);

        var metrics = ProbeMetrics.Read(result, ProbeResultShape.ScalarSeries);

        Assert.Equal(2, metrics["sent"]);
        Assert.Equal(10, metrics["min"], 3);
        Assert.Equal(20, metrics["max"], 3);
    }

    [Fact]
    public void Mos_OnlyForScalarSeries()
    {
        var scalar = Result(
        [
            new Sample { Sequence = 0, TimestampUtc = DateTimeOffset.UnixEpoch, Value = 10, Status = SampleStatus.Success },
        ]);

        Assert.True(ProbeMetrics.Read(scalar, ProbeResultShape.ScalarSeries).ContainsKey("mos"));

        // Оценка разговорной связи по сумме фаз HTTP — число, которое не про что.
        var phased = Result([Phase(0, "dns", 5), Phase(0, "ttfb", 200)]);

        Assert.False(ProbeMetrics.Read(phased, ProbeResultShape.PhasedTiming).ContainsKey("mos"));
    }

    [Fact]
    public void NumericFactsBecomeMetrics()
    {
        var result = Result(
            [Phase(0, "tls", 40)],
            [
                new ProbeFact
                {
                    Category = "tls",
                    Name = "Осталось дней",
                    Value = "62",
                    Numeric = 62,
                    Unit = MeasurementUnit.Count,
                },
            ]);

        var metrics = ProbeMetrics.Read(result, ProbeResultShape.PhasedTiming);

        Assert.Equal(62, metrics["Осталось дней"], 3);
    }

    [Theory]
    [InlineData("p95@ttfb", "p95")]
    [InlineData("loss@1.1.1.1", "loss")]
    [InlineData("p50", "p50")]
    public void BaseOf_StripsSeries(string metric, string expected) =>
        Assert.Equal(expected, ProbeMetrics.BaseOf(metric));

    [Fact]
    public void UnitOf_LooksThroughSeries() => Assert.Equal("мс", ProbeMetrics.UnitOf("p95@ttfb"));

    [Fact]
    public void Format_DoesNotClaimPrecisionMeasurementDoesNotHave()
    {
        // «244.16 мс» сообщает точность, которой у сетевого измерения нет.
        Assert.Equal("244 мс", ProbeMetrics.Format("p95", 244.163));
        Assert.Equal("4.2 мс", ProbeMetrics.Format("p50", 4.163));
        Assert.Equal("3.62", ProbeMetrics.Format("mos", 3.6234));
        Assert.Equal("5", ProbeMetrics.Format("sent", 5));
    }
}
