using StormMachine.Domain.Monitors;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Сравнение доступности за два соседних периода.
/// </summary>
/// <remarks>
/// Закрывает долг И-15: данные для ответа на «этот месяц против прошлого» есть с И-14,
/// механики не было.
/// <para>
/// Главная тонкость здесь не в арифметике. Доступность считается от <b>наблюдавшегося</b>
/// времени, поэтому 100 % при покрытии 4 % — это не отличный период, а период, который
/// почти не смотрели. Сравнение двух таких чисел даёт правдоподобный и бессмысленный
/// ответ, и продукт обязан сказать об этом раньше, чем оператор сделает вывод.
/// </para>
/// </remarks>
public sealed class AvailabilityComparisonTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static Availability Period(
        double uptimePercent,
        double coverage = 1.0,
        int incidents = 0,
        int days = 30)
    {
        var window = TimeSpan.FromDays(days);
        var observed = window * coverage;
        var down = observed * (1 - uptimePercent / 100);

        return new Availability
        {
            FromUtc = Now - window,
            ToUtc = Now,
            Observed = observed,
            Down = down,
            Incidents =
            [
                .. Enumerable.Range(0, incidents).Select(i => new AvailabilityIncident
                {
                    StartedUtc = Now.AddDays(-i - 1),
                    EndedUtc = Now.AddDays(-i - 1).AddMinutes(10),
                    Duration = TimeSpan.FromMinutes(10),
                    Checks = 2,
                    Summary = "цель не отвечает",
                }),
            ],
        };
    }

    [Fact]
    public void ImprovedAvailability_IsReportedAsGrowth()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(99.0),
            After = Period(99.9),
        };

        Assert.True(comparison.IsComparable);
        Assert.True(comparison.DeltaPercent > 0);
        Assert.Contains("выросла", comparison.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void DegradedAvailability_IsReportedAsDrop()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(99.9),
            After = Period(98.0),
        };

        Assert.True(comparison.DeltaPercent < 0);
        Assert.Contains("упала", comparison.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Разница в тысячные — это шум, а не новость.
    /// </summary>
    /// <remarks>
    /// Доступность считается из времени, и два периода не совпадают до нуля почти
    /// никогда. Называть изменением любую ненулевую разницу значило бы сообщать шум
    /// как событие и приучать оператора не читать эту строку.
    /// </remarks>
    [Fact]
    public void NoiseIsNotReportedAsChange()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(99.995),
            After = Period(99.999),
        };

        Assert.Contains("не изменилась", comparison.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Период с плохим покрытием сравнивать нельзя, и продукт это говорит.
    /// </summary>
    /// <remarks>
    /// Это и есть главное утверждение файла. Месяц, наблюдавшийся на четыре процента,
    /// формально даёт доступность — но она посчитана по тому немногому, что видели,
    /// и сравнивать её с полноценным месяцем нельзя. Промолчать здесь значило бы
    /// выдать отсутствие данных за хороший результат.
    /// </remarks>
    [Fact]
    public void PoorCoverage_BlocksTheComparison()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(100, coverage: 0.04),
            After = Period(99.0),
        };

        Assert.False(comparison.IsComparable);
        Assert.NotNull(comparison.Caveat);
        Assert.Contains("прошлый период", comparison.Caveat!, StringComparison.Ordinal);
        Assert.Contains("4 %", comparison.Caveat!, StringComparison.Ordinal);
    }

    /// <summary>Оговорка называет именно тот период, который наблюдался плохо.</summary>
    [Fact]
    public void Caveat_NamesTheGuiltyPeriod()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(99.0),
            After = Period(100, coverage: 0.1),
        };

        Assert.Contains("этот период", comparison.Caveat!, StringComparison.Ordinal);
    }

    /// <summary>Оговорка вытесняет вывод: сначала правда о данных, потом всё прочее.</summary>
    [Fact]
    public void Caveat_ReplacesTheConclusion()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(50, coverage: 0.2),
            After = Period(100),
        };

        Assert.DoesNotContain("выросла", comparison.Describe(), StringComparison.Ordinal);
        Assert.Equal(comparison.Caveat, comparison.Describe());
    }

    [Fact]
    public void IncidentCount_IsPartOfTheAnswer()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(99.0, incidents: 1),
            After = Period(98.0, incidents: 4),
        };

        Assert.Equal(3, comparison.DeltaIncidents);
        Assert.Contains("инцидентов больше на 3", comparison.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void FewerIncidents_AreNamedToo()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(99.0, incidents: 5),
            After = Period(99.5, incidents: 2),
        };

        Assert.Contains("инцидентов меньше на 3", comparison.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void DowntimeDelta_IsAvailable()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(99.0),
            After = Period(99.5),
        };

        // Простоя стало меньше — разница отрицательная.
        Assert.True(comparison.DeltaDown < TimeSpan.Zero);
    }

    /// <summary>Ровно половина покрытия — граница, и она проходима.</summary>
    [Fact]
    public void HalfCoverage_IsStillComparable()
    {
        var comparison = new AvailabilityComparison
        {
            Before = Period(99.0, coverage: 0.5),
            After = Period(99.0, coverage: 0.5),
        };

        Assert.True(comparison.IsComparable);
        Assert.Null(comparison.Caveat);
    }
}
