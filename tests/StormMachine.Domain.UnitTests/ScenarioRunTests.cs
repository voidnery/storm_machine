using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки итога сценария.
/// </summary>
/// <remarks>
/// Итог — худший из вердиктов шагов, а не средний и не последний: сценарий проверяет
/// цепочку, и одно сломанное звено делает непригодной всю. Пропущенный шаг при этом
/// не отказ: показать его отказом значило бы обвинить исправную часть цепочки
/// в поломке предыдущей.
/// </remarks>
public sealed class ScenarioRunTests
{
    private static ScenarioStepResult Step(string name, VerdictLevel level, bool skipped = false) => new()
    {
        Name = name,
        ProbeName = "ping",
        Verdict = level switch
        {
            VerdictLevel.Pass => Verdict.Pass("в норме"),
            VerdictLevel.Warn => new Verdict { Level = VerdictLevel.Warn, Summary = "предупреждение" },
            VerdictLevel.Fail => Verdict.Fail("сломалось"),
            _ => Verdict.NotEvaluated("не оценено"),
        },
        Duration = TimeSpan.FromSeconds(1),
        WasSkipped = skipped,
    };

    private static ScenarioRun Run(params ScenarioStepResult[] steps) => new()
    {
        Id = Guid.NewGuid(),
        ScenarioName = "тест",
        StartedUtc = DateTimeOffset.UnixEpoch,
        Steps = steps,
    };

    [Fact]
    public void Level_IsTheWorstOfSteps()
    {
        var run = Run(
            Step("a", VerdictLevel.Pass),
            Step("b", VerdictLevel.Fail),
            Step("c", VerdictLevel.Pass));

        Assert.Equal(VerdictLevel.Fail, run.Level);
    }

    [Fact]
    public void Level_WarnDoesNotBecomeFail()
    {
        var run = Run(Step("a", VerdictLevel.Pass), Step("b", VerdictLevel.Warn));

        Assert.Equal(VerdictLevel.Warn, run.Level);
    }

    [Fact]
    public void FirstFailure_NamesWhereItBroke()
    {
        var run = Run(
            Step("Разрешение имени", VerdictLevel.Pass),
            Step("Соединение", VerdictLevel.Fail),
            Step("Страница", VerdictLevel.Unknown, skipped: true));

        Assert.Equal("Соединение", run.FirstFailure?.Name);
    }

    [Fact]
    public void SkippedStep_IsNotAFailure()
    {
        var run = Run(Step("a", VerdictLevel.Pass), Step("b", VerdictLevel.Unknown, skipped: true));

        Assert.Null(run.FirstFailure);
    }

    [Fact]
    public void EmptyRun_IsUnknown() => Assert.Equal(VerdictLevel.Unknown, Run().Level);

    [Fact]
    public void PhaseMetric_DefaultsToMedian()
    {
        // Не время шага: оно задаётся числом проб и паузой между ними, и полоска
        // по нему сравнивала бы настройки замера, а не фазы.
        var step = new ScenarioStep
        {
            Name = "Соединение",
            ProbeName = "tcp",
            Target = Targets.Target.Parse("example.com"),
        };

        Assert.Equal("p50", step.PhaseMetric);
    }

    [Fact]
    public void ContinueOnFailure_IsOffByDefault()
    {
        var step = new ScenarioStep
        {
            Name = "Соединение",
            ProbeName = "tcp",
            Target = Targets.Target.Parse("example.com"),
        };

        Assert.False(step.ContinueOnFailure);
    }
}
