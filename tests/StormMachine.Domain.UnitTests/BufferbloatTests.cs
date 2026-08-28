using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки оценки bufferbloat.
/// </summary>
/// <remarks>
/// Закрепляется главное: оценивается <b>прирост</b> задержки, а не сама задержка.
/// Канал с холостыми 200 мс и приростом в 3 мс — это A+: он медленный, но очереди
/// в нём короткие, и разговор под загрузкой не порвётся. Спутать одно с другим значит
/// обвинить провайдера в том, чего он не делал.
/// </remarks>
public sealed class BufferbloatTests
{
    private static LatencyStatistics Stats(params double[] values) =>
        LatencyStatistics.Compute(
            [.. values.Select((v, i) => Sample.Ok(i, DateTimeOffset.UnixEpoch, v))]);

    private static BufferbloatAssessment Assessment(double idleP95, double loadedP95) => new()
    {
        // Ряд из одинаковых значений: p95 равен им же, и проверяется именно правило,
        // а не поведение перцентиля на выборке.
        Idle = Stats([.. Enumerable.Repeat(idleP95, 20)]),
        Loaded = Stats([.. Enumerable.Repeat(loadedP95, 20)]),
        Direction = "отдача",
        LoadMbps = 100,
    };

    [Theory]
    [InlineData(0, BufferbloatGrade.APlus)]
    [InlineData(4.9, BufferbloatGrade.APlus)]
    [InlineData(5, BufferbloatGrade.A)]
    [InlineData(29.9, BufferbloatGrade.A)]
    [InlineData(30, BufferbloatGrade.B)]
    [InlineData(59.9, BufferbloatGrade.B)]
    [InlineData(60, BufferbloatGrade.C)]
    [InlineData(199.9, BufferbloatGrade.C)]
    [InlineData(200, BufferbloatGrade.D)]
    [InlineData(399.9, BufferbloatGrade.D)]
    [InlineData(400, BufferbloatGrade.F)]
    [InlineData(5000, BufferbloatGrade.F)]
    public void GradeFor_FollowsTheScale(double increase, BufferbloatGrade expected) =>
        Assert.Equal(expected, BufferbloatAssessment.GradeFor(increase));

    [Fact]
    public void SlowChannelWithShortQueues_IsStillAPlus()
    {
        // Холостые 200 мс — это спутник или далёкая площадка. Очереди при этом короткие,
        // и разговор под загрузкой не порвётся: оценка про очереди, а не про скорость.
        var assessment = Assessment(idleP95: 200, loadedP95: 203);

        Assert.Equal(BufferbloatGrade.APlus, assessment.Grade);
        Assert.Equal(3, assessment.IncreaseMs, 3);
    }

    [Fact]
    public void FastChannelWithDeepQueues_IsF()
    {
        // Обратный случай: холостые 8 мс, под нагрузкой почти секунда. Канал быстрый
        // и негодный для разговора одновременно.
        var assessment = Assessment(idleP95: 8, loadedP95: 900);

        Assert.Equal(BufferbloatGrade.F, assessment.Grade);
        Assert.Equal(VerdictLevel.Fail, assessment.ToVerdict().Level);
    }

    [Fact]
    public void NegativeIncrease_IsNotAFailure()
    {
        // Под нагрузкой задержка иногда ниже холостой: канал прогрелся, маршрут
        // перестроился. Это не повод объявлять измерение неудачным.
        var assessment = Assessment(idleP95: 30, loadedP95: 25);

        Assert.Equal(BufferbloatGrade.APlus, assessment.Grade);
        Assert.True(assessment.IncreaseMs < 0);
    }

    [Fact]
    public void MissingPhase_IsNotEvaluated()
    {
        var assessment = new BufferbloatAssessment
        {
            Idle = Stats(10, 11, 12),
            Loaded = Stats(),
            Direction = "отдача",
        };

        Assert.Equal(BufferbloatGrade.Unknown, assessment.Grade);
        Assert.True(double.IsNaN(assessment.IncreaseMs));
        Assert.Equal(VerdictLevel.Unknown, assessment.ToVerdict().Level);
    }

    [Fact]
    public void Verdict_FailsFromCBecauseThatIsWhereSpeechBreaks()
    {
        Assert.Equal(VerdictLevel.Pass, Assessment(10, 12).ToVerdict().Level);
        Assert.Equal(VerdictLevel.Pass, Assessment(10, 30).ToVerdict().Level);
        Assert.Equal(VerdictLevel.Warn, Assessment(10, 55).ToVerdict().Level);
        Assert.Equal(VerdictLevel.Fail, Assessment(10, 100).ToVerdict().Level);
    }

    [Fact]
    public void Verdict_NamesBothNumbersNotJustTheLetter()
    {
        // Буква без числа бесполезна: шкалу знает мало кто, а спорить с провайдером
        // приходится числами.
        var summary = Assessment(idleP95: 12, loadedP95: 90).ToVerdict().Summary;

        Assert.Contains("78", summary, StringComparison.Ordinal);
        Assert.Contains("12", summary, StringComparison.Ordinal);
        Assert.Contains("90", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_SaysTheScaleIsAConventionNotAStandard()
    {
        var explanation = Assessment(10, 100).ToVerdict().Explanation ?? string.Empty;

        Assert.Contains("не стандарт", explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BufferbloatGrade.APlus, "A+")]
    [InlineData(BufferbloatGrade.F, "F")]
    [InlineData(BufferbloatGrade.Unknown, "—")]
    public void GradeLetter_IsWhatOperatorSees(BufferbloatGrade grade, string expected) =>
        Assert.Equal(expected, BufferbloatAssessment.GradeLetter(grade));

    [Fact]
    public void Explain_TalksAboutWhatBreaks()
    {
        // Буква ничего не говорит тому, кто не знает шкалы. Объяснение названо
        // через то, что человек заметит.
        Assert.Contains("разговор", BufferbloatAssessment.Explain(BufferbloatGrade.C),
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("разговор", BufferbloatAssessment.Explain(BufferbloatGrade.F),
            StringComparison.OrdinalIgnoreCase);
    }
}
