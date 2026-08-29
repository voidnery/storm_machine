using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Пороги активного профиля применяются к измерению.
/// </summary>
/// <remarks>
/// Закрывает долг И-16: профиль хранил свои пороги и показывал их, а подставлять
/// в измерение приходилось руками — механика была, связки не было.
/// <para>
/// Пороги заводят отдельно на каждое место именно потому, что «хорошо» от места
/// зависит: 5 мс до шлюза в офисе — норма, 5 мс до филиала за тысячу километров —
/// физически невозможно. Профиль без применения этих порогов был наполовину
/// украшением.
/// </para>
/// </remarks>
public sealed class ProfileThresholdTests
{
    private static NetworkProfile Strict(params Threshold[] thresholds) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Офис",
        IsActive = true,
        Thresholds = thresholds,
    };

    private static async Task<RunOutcome> RunAsync(NetworkProfile? profile, double value)
    {
        var profiles = new FakeProfileStore();

        if (profile is not null)
        {
            await profiles.SaveAsync(profile);
            await profiles.ActivateAsync(profile.Id);
        }

        var orchestrator = new RunOrchestrator(
            new NullRunStore(),
            new NullClock(),
            new NullEnvironment(),
            profiles);

        return await orchestrator.RunAsync(
            new FakeProbe(() => value),
            new ProbeRequest
            {
                Target = Target.Ip("127.0.0.1"),
                Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            },
            new RunOptions());
    }

    [Fact]
    public async Task MeasurementWithinTheProfileThreshold_Passes()
    {
        var outcome = await RunAsync(
            Strict(new Threshold { Metric = "p95", Comparison = Comparison.LessThan, Value = 100 }),
            value: 10);

        Assert.NotNull(outcome.ProfileVerdict);
        Assert.Equal(VerdictLevel.Pass, outcome.ProfileVerdict!.Level);
        Assert.Equal("Офис", outcome.ProfileName);
    }

    [Fact]
    public async Task MeasurementOverTheProfileThreshold_Fails()
    {
        var outcome = await RunAsync(
            Strict(new Threshold { Metric = "p95", Comparison = Comparison.LessThan, Value = 5 }),
            value: 50);

        Assert.Equal(VerdictLevel.Fail, outcome.ProfileVerdict!.Level);
        Assert.Contains("p95", outcome.ProfileVerdict.Summary, StringComparison.Ordinal);
    }

    /// <summary>Без активного профиля судить не по чему, и продукт не судит.</summary>
    [Fact]
    public async Task WithoutAnActiveProfile_ThereIsNoVerdict()
    {
        var outcome = await RunAsync(null, value: 50);

        Assert.Null(outcome.ProfileVerdict);
        Assert.Null(outcome.ProfileName);
    }

    /// <summary>
    /// Профиль без порогов не даёт вердикта, но остаётся в условиях измерения.
    /// </summary>
    /// <remarks>
    /// Разделение существенное: имя профиля — часть условий, по которым потом сравнивают
    /// измерения между собой, а пороги — суждение. Профиль, заведённый только ради
    /// пометки о месте, законен и вердикта выносить не должен.
    /// </remarks>
    [Fact]
    public async Task ProfileWithoutThresholds_StillMarksThePlace()
    {
        var outcome = await RunAsync(Strict(), value: 50);

        Assert.Null(outcome.ProfileVerdict);
        Assert.Equal("Офис", outcome.ProfileName);
        Assert.Equal("Офис", outcome.Result.Context.Profile);
    }

    /// <summary>
    /// Проба без единого ответа порогами не оценивается.
    /// </summary>
    /// <remarks>
    /// У неё нет величины, с которой пороги сравнивать, и вердикт «не прошло по порогу»
    /// подменил бы причину: недоступная цель — это недоступная цель, а не превышенная
    /// задержка. Оператор, прочитавший «p95 выше порога» там, где цель молчала,
    /// пойдёт чинить не то.
    /// </remarks>
    [Fact]
    public async Task ProbeWithNoAnswers_IsNotJudgedByThresholds()
    {
        var profiles = new FakeProfileStore();
        var profile = Strict(new Threshold { Metric = "p95", Comparison = Comparison.LessThan, Value = 5 });

        await profiles.SaveAsync(profile);
        await profiles.ActivateAsync(profile.Id);

        var orchestrator = new RunOrchestrator(
            new NullRunStore(),
            new NullClock(),
            new NullEnvironment(),
            profiles);

        var outcome = await orchestrator.RunAsync(
            new SilentProbe(),
            new ProbeRequest
            {
                Target = Target.Ip("127.0.0.1"),
                Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            },
            new RunOptions());

        Assert.Equal(0, outcome.Result.SuccessCount);
        Assert.Null(outcome.ProfileVerdict);
    }

    /// <summary>Проба, у которой ни один ответ не пришёл.</summary>
    private sealed class SilentProbe : IProbe
    {
        public ProbeDescriptor Descriptor { get; } = new()
        {
            Kind = ProbeKind.Icmp,
            Shape = ProbeResultShape.ScalarSeries,
            Name = "silent",
            Title = "Молчащая цель",
            Description = "Ни одного ответа.",
            Unit = Domain.Measurements.MeasurementUnit.Milliseconds,
            Methodology = Domain.Measurements.Methodology.IcmpEcho,
            Parameters = [],
        };

        public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request) => [];

        public async IAsyncEnumerable<Domain.Measurements.Sample> ExecuteAsync(
            ProbeRequest request,
            IProbeObserver observer,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < 3; i++)
            {
                yield return new Domain.Measurements.Sample
                {
                    Sequence = i,
                    TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(i),
                    Value = 0,
                    Status = Domain.Measurements.SampleStatus.Timeout,
                };
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
