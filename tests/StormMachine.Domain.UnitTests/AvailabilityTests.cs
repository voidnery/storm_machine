using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Расчёт доступности.
/// </summary>
/// <remarks>
/// Главное, что здесь проверяется, — невозможность прочитать числа выгоднее, чем есть.
/// Время, которое никто не наблюдал, не должно попадать ни в исправную работу,
/// ни в простой, а доступность 100% при покрытии 4% обязана быть отличима
/// от доступности 100% при покрытии 100%.
/// </remarks>
public sealed class AvailabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);

    private static MonitorCheck Check(int minute, VerdictLevel level, CheckKind kind = CheckKind.Measured) => new()
    {
        Id = Guid.NewGuid(),
        MonitorId = Guid.Empty,
        StartedUtc = Start.AddMinutes(minute),
        Kind = kind,
        Level = level,
        Summary = level == VerdictLevel.Fail ? "цель не отвечает" : "норма",
    };

    [Fact(DisplayName = "Без проверок доступность не считается, а не равна ста процентам")]
    public void NoChecksIsNotPerfect()
    {
        var result = AvailabilityCalculator.Compute([], Start, Start.AddHours(1));

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.UptimePercent);
        Assert.Equal(0, result.Coverage);
        Assert.Equal(TimeSpan.FromHours(1), result.Unobserved);
    }

    [Fact(DisplayName = "Доступность считается по времени, а не по числу проверок")]
    public void ByTimeNotByCount()
    {
        // Десять проверок с шагом в минуту, из них две подряд — отказ.
        var checks = new List<MonitorCheck>();

        for (var i = 0; i < 10; i++)
        {
            checks.Add(Check(i, i is 4 or 5 ? VerdictLevel.Fail : VerdictLevel.Pass));
        }

        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(10));

        Assert.Equal(10, result.Total);
        Assert.Equal(2, result.Fail);
        Assert.Equal(TimeSpan.FromMinutes(10), result.Observed);
        Assert.Equal(TimeSpan.FromMinutes(2), result.Down);
        Assert.Equal(80, result.UptimePercent, 3);
    }

    [Fact(DisplayName = "Предупреждение простоем не считается")]
    public void WarnIsNotDown()
    {
        var checks = new List<MonitorCheck>
        {
            Check(0, VerdictLevel.Pass),
            Check(1, VerdictLevel.Warn),
            Check(2, VerdictLevel.Pass),
        };

        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(3));

        Assert.Equal(1, result.Warn);
        Assert.Equal(TimeSpan.Zero, result.Down);
        Assert.Equal(100, result.UptimePercent, 3);
    }

    [Fact(DisplayName = "Обслуживание исключается из знаменателя")]
    public void MaintenanceExcluded()
    {
        var checks = new List<MonitorCheck>
        {
            Check(0, VerdictLevel.Pass),
            Check(10, VerdictLevel.Unknown, CheckKind.Maintenance),
            Check(40, VerdictLevel.Pass),
        };

        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(60));

        // Тридцать минут работ не считаются ни исправной работой, ни простоем.
        Assert.Equal(TimeSpan.FromMinutes(30), result.Maintenance);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Observed);
        Assert.Equal(100, result.UptimePercent, 3);
    }

    [Fact(DisplayName = "Ненаблюдавшееся время не считается ни работой, ни простоем")]
    public void UnobservedExcluded()
    {
        var checks = new List<MonitorCheck>
        {
            Check(0, VerdictLevel.Pass),
            Check(10, VerdictLevel.Unknown, CheckKind.Missed),
            Check(50, VerdictLevel.Pass),
        };

        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(60));

        Assert.Equal(TimeSpan.FromMinutes(40), result.Unobserved);
        Assert.Equal(TimeSpan.FromMinutes(20), result.Observed);
        Assert.Equal(100, result.UptimePercent, 3);
    }

    [Fact(DisplayName = "Покрытие отличает отличную сеть от отсутствия данных")]
    public void CoverageSeparatesLuckFromEvidence()
    {
        var thin = new List<MonitorCheck> { Check(0, VerdictLevel.Pass), Check(1, VerdictLevel.Unknown, CheckKind.Missed) };
        var thick = new List<MonitorCheck>();

        for (var i = 0; i < 60; i++)
        {
            thick.Add(Check(i, VerdictLevel.Pass));
        }

        var poor = AvailabilityCalculator.Compute(thin, Start, Start.AddMinutes(60));
        var good = AvailabilityCalculator.Compute(thick, Start, Start.AddMinutes(60));

        // Доступность у обоих сто процентов — и это ровно тот случай, ради которого
        // покрытие существует.
        Assert.Equal(100, poor.UptimePercent, 3);
        Assert.Equal(100, good.UptimePercent, 3);

        Assert.True(poor.Coverage < 0.05, $"покрытие бедного ряда: {poor.Coverage}");
        Assert.True(good.Coverage > 0.95, $"покрытие полного ряда: {good.Coverage}");
    }

    [Fact(DisplayName = "Инцидент — это непрерывная цепочка отказов")]
    public void IncidentIsAContiguousRun()
    {
        var checks = new List<MonitorCheck>
        {
            Check(0, VerdictLevel.Pass),
            Check(1, VerdictLevel.Fail),
            Check(2, VerdictLevel.Fail),
            Check(3, VerdictLevel.Fail),
            Check(4, VerdictLevel.Pass),
            Check(5, VerdictLevel.Pass),
            Check(6, VerdictLevel.Fail),
            Check(7, VerdictLevel.Pass),
        };

        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(8));

        Assert.Equal(2, result.Incidents.Count);
        Assert.Equal(TimeSpan.FromMinutes(3), result.Incidents[0].Duration);
        Assert.Equal(3, result.Incidents[0].Checks);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Incidents[1].Duration);
        Assert.All(result.Incidents, i => Assert.False(i.IsOpen));
    }

    [Fact(DisplayName = "Незакрытый инцидент виден как идущий и в среднее не входит")]
    public void OpenIncident()
    {
        var checks = new List<MonitorCheck>
        {
            Check(0, VerdictLevel.Pass),
            Check(1, VerdictLevel.Fail),
            Check(2, VerdictLevel.Fail),
        };

        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(5));

        Assert.Single(result.Incidents);
        Assert.True(result.Incidents[0].IsOpen);

        // Восстановление считать не по чему: инцидент ещё не кончился, и его
        // длительность неизвестна. Подставить сюда текущую значило бы занижать.
        Assert.Null(result.MeanTimeToRecovery);
    }

    [Fact(DisplayName = "Пропуск закрывает инцидент, а не продлевает его")]
    public void MissedClosesIncident()
    {
        var checks = new List<MonitorCheck>
        {
            Check(0, VerdictLevel.Fail),
            Check(1, VerdictLevel.Unknown, CheckKind.Missed),
            Check(30, VerdictLevel.Pass),
        };

        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(40));

        // Тянуть простой через время, которого мы не видели, значило бы записать
        // в отказ чужой сон.
        Assert.Single(result.Incidents);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Incidents[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Down);
    }

    [Fact(DisplayName = "Бюджет ошибок считается от наблюдавшегося времени")]
    public void ErrorBudget()
    {
        var checks = new List<MonitorCheck>();

        for (var i = 0; i < 100; i++)
        {
            checks.Add(Check(i, i is 40 or 41 ? VerdictLevel.Fail : VerdictLevel.Pass));
        }

        var objective = new ServiceLevelObjective { TargetPercent = 99, Window = TimeSpan.FromMinutes(100) };
        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(100), objective);

        // Сто минут наблюдений, цель 99% — бюджет одна минута, израсходовано две.
        Assert.Equal(TimeSpan.FromMinutes(1), result.ErrorBudget);
        Assert.Equal(TimeSpan.Zero, result.ErrorBudgetLeft);
        Assert.Equal(200, result.ErrorBudgetUsedPercent!.Value, 1);
        Assert.False(result.IsMet);
    }

    [Fact(DisplayName = "Выполненная цель видна как выполненная")]
    public void ObjectiveMet()
    {
        var checks = new List<MonitorCheck>();

        for (var i = 0; i < 1000; i++)
        {
            checks.Add(Check(i, i == 500 ? VerdictLevel.Fail : VerdictLevel.Pass));
        }

        var objective = new ServiceLevelObjective { TargetPercent = 99, Window = TimeSpan.FromMinutes(1000) };
        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(1000), objective);

        Assert.True(result.IsMet);
        Assert.Equal(99.9, result.UptimePercent, 3);
        Assert.True(result.ErrorBudgetLeft > TimeSpan.Zero);
    }

    [Fact(DisplayName = "Точность границ простоя равна шагу проверок")]
    public void ResolutionIsTheCheckInterval()
    {
        var checks = new List<MonitorCheck>();

        for (var i = 0; i < 20; i++)
        {
            checks.Add(Check(i * 5, VerdictLevel.Pass));
        }

        var result = AvailabilityCalculator.Compute(checks, Start, Start.AddMinutes(100));

        // «14 минут» и «14 минут ± 5» — разные утверждения, и второе честнее.
        Assert.Equal(TimeSpan.FromMinutes(5), result.Resolution);
    }
}
