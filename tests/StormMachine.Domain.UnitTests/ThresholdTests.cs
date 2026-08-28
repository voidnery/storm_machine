using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки порогов и вердикта по ним.
/// </summary>
/// <remarks>
/// Ключевое здесь — что порог, для которого проба не даёт метрики, не считается
/// соблюдённым. Выдать молчание за успех значило бы сообщить оператору, что проверено
/// то, чего не проверяли, — и это худшая из возможных ошибок диагностического
/// инструмента, потому что она не видна.
/// </remarks>
public sealed class ThresholdTests
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

    private static ProbeResult Result(int successes, int failures, double value = 10) => new()
    {
        Id = Guid.NewGuid(),
        Kind = ProbeKind.Icmp,
        Target = Target.Parse("example.com"),
        Context = TestContext,
        Unit = MeasurementUnit.Milliseconds,
        Samples =
        [
            .. Enumerable.Range(0, successes).Select(i => new Sample
            {
                Sequence = i,
                TimestampUtc = DateTimeOffset.UnixEpoch,
                Value = value,
                Status = SampleStatus.Success,
            }),
            .. Enumerable.Range(successes, failures).Select(i =>
                Sample.Failed(i, DateTimeOffset.UnixEpoch, SampleStatus.Timeout)),
        ],
        CompletedUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Parse_LongSignsBeforeShort()
    {
        // Иначе «<=» распалось бы на «<» и «=», и порог «p95 <= 50» стал бы «p95 < =50».
        var threshold = Threshold.Parse("p95 <= 50");

        Assert.Equal(Comparison.AtMost, threshold.Comparison);
        Assert.Equal("p95", threshold.Metric);
        Assert.Equal(50, threshold.Value);
    }

    [Fact]
    public void Parse_AcceptsRussianMetricNames()
    {
        var threshold = Threshold.Parse("Осталось дней >= 14");

        Assert.Equal("Осталось дней", threshold.Metric);
        Assert.Equal(Comparison.AtLeast, threshold.Comparison);
    }

    [Fact]
    public void Parse_AcceptsSeriesMetric()
    {
        var threshold = Threshold.Parse("p95@ttfb < 800");

        Assert.Equal("p95@ttfb", threshold.Metric);
    }

    [Fact]
    public void Parse_RejectsNonsense() => Assert.Throws<FormatException>(() => Threshold.Parse("быстро"));

    [Fact]
    public void NoAnswers_FailsWithoutConsultingThresholds()
    {
        // «p95 в норме» на пустом ряду означало бы, что всё хорошо там,
        // где не отвечает вообще ничего.
        var verdict = ThresholdEvaluator.Evaluate(Result(0, 5), [Threshold.Parse("p95 < 1000")]);

        Assert.Equal(VerdictLevel.Fail, verdict.Level);
    }

    [Fact]
    public void RefusedAnswers_AreNotCalledSilence()
    {
        // Резолвер, ответивший NXDOMAIN за четыре миллисекунды, работает безупречно:
        // не существует имени. Назвать это молчанием значило бы отправить оператора
        // чинить сеть там, где чинить надо запись.
        var refused = new ProbeResult
        {
            Id = Guid.NewGuid(),
            Kind = ProbeKind.Dns,
            Target = Target.Parse("нет-такого.invalid"),
            Context = TestContext,
            Unit = MeasurementUnit.Milliseconds,
            Samples =
            [
                new Sample
                {
                    Sequence = 0,
                    TimestampUtc = DateTimeOffset.UnixEpoch,
                    Value = 4,
                    Status = SampleStatus.Rejected,
                    RespondedBy = "NXDOMAIN",
                },
            ],
            CompletedUtc = DateTimeOffset.UnixEpoch,
        };

        var verdict = ThresholdEvaluator.Evaluate(refused, [Threshold.Parse("p95 < 1000")]);

        Assert.Equal(VerdictLevel.Fail, verdict.Level);
        Assert.Contains("NXDOMAIN", verdict.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("не получил ответа", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TimedOutAnswers_AreCalledSilence()
    {
        var verdict = ThresholdEvaluator.Evaluate(Result(0, 3), [Threshold.Parse("p95 < 1000")]);

        Assert.Contains("не получил ответа", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoThresholds_IsNotEvaluated()
    {
        var verdict = ThresholdEvaluator.Evaluate(Result(3, 0), []);

        Assert.Equal(VerdictLevel.Unknown, verdict.Level);
    }

    [Fact]
    public void MissingMetric_IsNotAPass()
    {
        var verdict = ThresholdEvaluator.Evaluate(Result(3, 0), [Threshold.Parse("Осталось дней >= 14")]);

        Assert.Equal(VerdictLevel.Unknown, verdict.Level);
        Assert.Contains("Осталось дней", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMetric_DoesNotHideAPassOfTheOthers()
    {
        var verdict = ThresholdEvaluator.Evaluate(
            Result(3, 0),
            [Threshold.Parse("p95 < 1000"), Threshold.Parse("Осталось дней >= 14")]);

        Assert.Equal(VerdictLevel.Pass, verdict.Level);
        Assert.Contains("Осталось дней", verdict.Explanation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void WorstViolationSetsTheLevel()
    {
        var verdict = ThresholdEvaluator.Evaluate(
            Result(3, 0, value: 500),
            [
                Threshold.Parse("p95 < 100", VerdictLevel.Warn),
                Threshold.Parse("p95 < 200"),
            ]);

        // Одно нарушение уровня «отказ» важнее любого числа предупреждений.
        Assert.Equal(VerdictLevel.Fail, verdict.Level);
    }
}
