using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.UnitTests;

public sealed class ProbeResultTests
{
    private static ProbeResult Result(params Sample[] samples) => new()
    {
        Id = Guid.NewGuid(),
        Kind = ProbeKind.Icmp,
        Target = Target.Ip("192.168.1.1"),
        Unit = MeasurementUnit.Milliseconds,
        Samples = samples,
        CompletedUtc = DateTimeOffset.UnixEpoch,
        Context = new MeasurementContext
        {
            InterfaceName = "test",
            AdapterKind = AdapterKind.Physical,
            CalibrationBaselineMs = 0,
            ProductVersion = "0.1.0",
            Methodology = Methodology.IcmpEcho,
            StartedUtc = DateTimeOffset.UnixEpoch,
        },
    };

    private static Sample Ok(int sequence) =>
        Sample.Ok(sequence, DateTimeOffset.UnixEpoch, 0.5);

    private static Sample Lost(int sequence) =>
        Sample.Failed(sequence, DateTimeOffset.UnixEpoch, SampleStatus.Timeout);

    [Fact]
    public void CountsSuccessesAndLosses()
    {
        var result = Result(Ok(0), Ok(1), Lost(2), Ok(3));

        Assert.Equal(4, result.SentCount);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(1, result.LostCount);
        Assert.Equal(25.0, result.LossPercent, 3);
    }

    [Fact]
    public void EmptyResult_DoesNotDivideByZero()
    {
        var result = Result();

        Assert.Equal(0, result.SentCount);
        Assert.Equal(0.0, result.LossPercent);
    }

    [Fact]
    public void CancelledRun_KeepsWhatWasMeasured()
    {
        // Требование отказоустойчивости: прерванный тест сохраняет измеренное
        // (docs/01-analysis.md §9.5, принцип 5).
        var result = Result(Ok(0), Ok(1)) with { WasCancelled = true };

        Assert.True(result.WasCancelled);
        Assert.Equal(2, result.SuccessCount);
    }

    [Fact]
    public void FailedSample_HasNoValue()
    {
        var lost = Lost(0);

        Assert.False(lost.IsSuccess);
        Assert.True(double.IsNaN(lost.Value));
    }

    [Fact]
    public void Verdict_ExplainsItself()
    {
        // UX-принцип «объяснимость»: рядом с вердиктом видно, какая метрика и какой порог его дали.
        var verdict = Verdict.Fail("Потери выше допустимых", "loss", 12.0, 1.0);

        Assert.Equal(VerdictLevel.Fail, verdict.Level);
        Assert.NotNull(verdict.Reasoning);
        Assert.Contains("loss", verdict.Reasoning, StringComparison.Ordinal);
        Assert.Contains("12", verdict.Reasoning, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_WithoutThresholds_HasNoReasoning()
    {
        var verdict = Verdict.NotEvaluated();

        Assert.Equal(VerdictLevel.Unknown, verdict.Level);
        Assert.Null(verdict.Reasoning);
    }
}
