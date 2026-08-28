using StormMachine.Domain.Measurements;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Эталон и сравнение с ним.
/// </summary>
/// <remarks>
/// Главное, что здесь закрепляется, — сравнение не выдаёт за изменение то, что
/// изменением не является: разброс сети, накладные расходы измерения и смену
/// условий. Продукт, объявляющий ухудшением каждый чих, за неделю обесценивает
/// собственное слово.
/// </remarks>
public sealed class BaselineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static MeasurementContext Context(
        AdapterKind adapter = AdapterKind.Physical,
        double calibration = 0.2,
        string @interface = "Ethernet",
        string? backend = null,
        string version = "0.1.0") => new()
        {
            InterfaceName = @interface,
            AdapterKind = adapter,
            CalibrationBaselineMs = calibration,
            ProductVersion = version,
            Methodology = Methodology.IcmpEcho,
            Backend = backend,
            StartedUtc = Noon,
        };

    private static Baseline Sample(params BaselineMetric[] metrics) => new()
    {
        Id = Guid.NewGuid(),
        Name = "норма",
        Subject = "ping",
        Target = Target.Ip("192.168.1.1"),
        Unit = MeasurementUnit.Milliseconds,
        Context = Context(),
        Metrics = metrics.Length > 0 ? metrics : [new BaselineMetric("p95", 100, HigherIsBetter: false)],
        CapturedUtc = Noon,
    };

    // ------------------------------------------------------------ направление

    [Theory(DisplayName = "Куда «лучше» определяется по имени метрики раньше, чем по единице")]
    [InlineData("loss", MeasurementUnit.Percent, false)]
    [InlineData("jitter", MeasurementUnit.Milliseconds, false)]
    [InlineData("pdv", MeasurementUnit.Milliseconds, false)]
    [InlineData("mos", MeasurementUnit.Count, true)]
    [InlineData("uptime", MeasurementUnit.Percent, true)]
    [InlineData("p95", MeasurementUnit.Milliseconds, false)]
    [InlineData("p95", MeasurementUnit.MegabitsPerSecond, true)]
    public void DirectionIsChosenByNameThenUnit(string metric, MeasurementUnit unit, bool higherIsBetter) =>
        Assert.Equal(higherIsBetter, Baseline.HigherIsBetterFor(metric, unit));

    [Fact(DisplayName = "Уточнение ряда не мешает определить направление")]
    public void DirectionIgnoresSeriesSuffix() =>
        Assert.False(Baseline.HigherIsBetterFor("p95@ttfb", MeasurementUnit.Milliseconds));

    [Theory(DisplayName = "Счётчики проб в эталон не годятся")]
    [InlineData("sent")]
    [InlineData("received")]
    [InlineData("p95@ttfb")]
    public void CountsAndSeriesAreNotComparable(string metric) => Assert.False(Baseline.IsComparable(metric));

    [Theory(DisplayName = "Метрики сети в эталон годятся")]
    [InlineData("p50")]
    [InlineData("loss")]
    [InlineData("jitter")]
    public void NetworkMetricsAreComparable(string metric) => Assert.True(Baseline.IsComparable(metric));

    // ------------------------------------------------------------- значимость

    [Fact(DisplayName = "Сдвиг меньше пяти процентов изменением не считается")]
    public void SmallRelativeShiftIsNoise()
    {
        var baseline = Sample(new BaselineMetric("p95", 100, HigherIsBetter: false));

        // Сто три против ста — это разброс, с которым сеть расходится сама с собой.
        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 103 },
            Context());

        var change = Assert.Single(result.Changes);

        Assert.Equal(ChangeDirection.Same, change.Direction);
        Assert.Contains("5 %", change.Insignificance!, StringComparison.Ordinal);
        Assert.Equal("без изменений", result.Verdict);
    }

    [Fact(DisplayName = "Сдвиг ниже порога достоверности изменением не считается")]
    public void ShiftBelowCalibrationFloorIsNoise()
    {
        // Порог 0.5 мс, а метрика мелкая: относительно это 40 %, по существу —
        // накладные расходы измерительного стека, а не сеть.
        var baseline = Sample(new BaselineMetric("p50", 0.5, HigherIsBetter: false));

        var result = BaselineComparer.Compare(
            baseline with { Context = Context(calibration: 0.5) },
            new Dictionary<string, double> { ["p50"] = 0.7 },
            Context(calibration: 0.5));

        var change = Assert.Single(result.Changes);

        Assert.Equal(ChangeDirection.Same, change.Direction);
        Assert.Contains("порога достоверности", change.Insignificance!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Берётся больший из двух порогов достоверности")]
    public void FloorIsTheLargerOfTwo()
    {
        // Эталон снят на разгруженной машине, текущее — на загруженной. Доверять
        // можно только худшему из двух: точность общая, а не по выбору.
        var baseline = Sample(new BaselineMetric("p50", 1, HigherIsBetter: false));

        var result = BaselineComparer.Compare(
            baseline with { Context = Context(calibration: 0.05) },
            new Dictionary<string, double> { ["p50"] = 1.4 },
            Context(calibration: 0.6));

        Assert.Equal(ChangeDirection.Same, Assert.Single(result.Changes).Direction);
    }

    [Fact(DisplayName = "Значимое ухудшение названо ухудшением")]
    public void RealRegressionIsReported()
    {
        var result = BaselineComparer.Compare(
            Sample(new BaselineMetric("p95", 100, HigherIsBetter: false)),
            new Dictionary<string, double> { ["p95"] = 180 },
            Context());

        Assert.Equal(ChangeDirection.Worse, Assert.Single(result.Changes).Direction);
        Assert.Equal("стало хуже", result.Verdict);
    }

    [Fact(DisplayName = "Для метрики, где больше — лучше, рост считается улучшением")]
    public void HigherIsBetterFlipsTheVerdict()
    {
        var baseline = Sample(new BaselineMetric("p50", 100, HigherIsBetter: true)) with
        {
            Unit = MeasurementUnit.MegabitsPerSecond,
        };

        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p50"] = 180 },
            Context());

        Assert.Equal(ChangeDirection.Better, Assert.Single(result.Changes).Direction);
    }

    [Fact(DisplayName = "Разнонаправленные изменения не сводятся к одному слову")]
    public void MixedChangesStayMixed()
    {
        var baseline = Sample(
            new BaselineMetric("p95", 100, HigherIsBetter: false),
            new BaselineMetric("loss", 5, HigherIsBetter: false));

        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 50, ["loss"] = 12 },
            Context());

        Assert.Equal(1, result.BetterCount);
        Assert.Equal(1, result.WorseCount);
        Assert.StartsWith("смешанно", result.Verdict, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------- условия

    [Fact(DisplayName = "Смена типа адаптера — тяжёлое расхождение условий")]
    public void AdapterChangeIsSevere()
    {
        // Эталон по Wi-Fi против измерения по кабелю даёт красивую цифру улучшения,
        // которого не было: изменился не канал, а способ смотреть на него.
        var baseline = Sample() with { Context = Context(AdapterKind.Wireless) };

        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 40 },
            Context(AdapterKind.Physical));

        Assert.True(result.HasSevereMismatch);
        Assert.Contains(result.Mismatches, m => m.What == "тип адаптера");

        // Сравнение при этом не запрещено: бывает, что оно и нужно.
        Assert.Equal(ChangeDirection.Better, Assert.Single(result.Changes).Direction);
    }

    [Fact(DisplayName = "Смена внешней службы — тяжёлое расхождение условий")]
    public void BackendChangeIsSevere()
    {
        var baseline = Sample() with { Context = Context(backend: "NDT7") };

        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 100 },
            Context(backend: "iperf3"));

        Assert.True(result.HasSevereMismatch);
    }

    [Fact(DisplayName = "Смена интерфейса и версии — расхождения, но не тяжёлые")]
    public void MinorMismatchesAreNotSevere()
    {
        var result = BaselineComparer.Compare(
            Sample(),
            new Dictionary<string, double> { ["p95"] = 100 },
            Context(@interface: "Wi-Fi 6", version: "0.2.0"));

        Assert.False(result.HasSevereMismatch);
        Assert.Equal(2, result.Mismatches.Count);
    }

    [Fact(DisplayName = "Двукратный уход порога достоверности назван")]
    public void CalibrationDriftIsReported()
    {
        var result = BaselineComparer.Compare(
            Sample() with { Context = Context(calibration: 0.2) },
            new Dictionary<string, double> { ["p95"] = 100 },
            Context(calibration: 0.9));

        Assert.Contains(result.Mismatches, m => m.What == "порог достоверности");
    }

    [Fact(DisplayName = "Одинаковые условия расхождений не дают")]
    public void SameConditionsProduceNoMismatch()
    {
        var result = BaselineComparer.Compare(
            Sample(),
            new Dictionary<string, double> { ["p95"] = 100 },
            Context());

        Assert.Empty(result.Mismatches);
    }

    // ------------------------------------------------------------- пропавшее

    [Fact(DisplayName = "Метрика, которой нет в текущем измерении, названа пропавшей")]
    public void MissingMetricIsNamed()
    {
        var baseline = Sample(
            new BaselineMetric("p95", 100, HigherIsBetter: false),
            new BaselineMetric("mos", 4.2, HigherIsBetter: true));

        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 100 },
            Context());

        // Молча пропустить её значило бы показать сравнение полным, когда оно неполно.
        Assert.Equal(["mos"], result.Missing);
        Assert.Single(result.Changes);
    }
}
