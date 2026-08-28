using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Гистерезис оповещений.
/// </summary>
/// <remarks>
/// Вторая половина приёмки И-14: «алерт срабатывает по порогу и не дребезжит
/// на границе». Здесь проверяется именно граница — метрика, севшая ровно на пороге
/// и качающаяся вокруг него.
/// </remarks>
public sealed class AlertHysteresisTests
{
    private static readonly Threshold Limit = Threshold.Parse("p95 < 100");
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static MonitorCheck Check(double value, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        MonitorId = Guid.Empty,
        StartedUtc = at,
        Level = Limit.IsSatisfiedBy(value) ? VerdictLevel.Pass : VerdictLevel.Fail,
        Summary = $"p95 = {value}",
        Metric = "p95",
        Value = value,
        Threshold = Limit.Value,
    };

    /// <summary>Прогоняет ряд значений и возвращает все состоявшиеся события.</summary>
    private static (AlertState State, List<AlertAction> Events, List<AlertAction> Notified) Run(
        AlertRule rule,
        params double[] values)
    {
        var state = AlertState.Clear;
        var events = new List<AlertAction>();
        var notified = new List<AlertAction>();
        var at = Start;

        foreach (var value in values)
        {
            var decision = AlertEvaluator.Apply(state, Check(value, at), Limit, rule, at);

            state = decision.State;

            if (decision.Action != AlertAction.None)
            {
                events.Add(decision.Action);

                if (decision.Notify)
                {
                    notified.Add(decision.Action);
                }
            }

            at = at.AddMinutes(1);
        }

        return (state, events, notified);
    }

    [Fact(DisplayName = "Одиночный выброс алерт не поднимает")]
    public void SingleSpikeIsIgnored()
    {
        var (state, events, _) = Run(new AlertRule { Cooldown = TimeSpan.Zero }, 50, 150, 50, 50);

        Assert.False(state.IsRaised);
        Assert.Empty(events);
    }

    [Fact(DisplayName = "Два нарушения подряд поднимают алерт")]
    public void TwoInARowRaise()
    {
        var (state, events, _) = Run(new AlertRule { Cooldown = TimeSpan.Zero }, 50, 150, 150);

        Assert.True(state.IsRaised);
        Assert.Equal([AlertAction.Raised], events);
    }

    [Fact(DisplayName = "Одна удачная проверка алерт не снимает")]
    public void SingleGoodDoesNotClear()
    {
        var (state, events, _) = Run(new AlertRule { Cooldown = TimeSpan.Zero }, 150, 150, 50, 150);

        Assert.True(state.IsRaised);
        Assert.Equal([AlertAction.Raised], events);
    }

    [Fact(DisplayName = "Две удачные подряд снимают алерт")]
    public void TwoGoodClear()
    {
        var (state, events, _) = Run(new AlertRule { Cooldown = TimeSpan.Zero }, 150, 150, 50, 50);

        Assert.False(state.IsRaised);
        Assert.Equal([AlertAction.Raised, AlertAction.Cleared], events);
    }

    // ------------------------------------------------------- то, ради чего всё

    [Fact(DisplayName = "Метрика на самом пороге не даёт дребезга")]
    public void NoChatterAtTheThreshold()
    {
        // Классический случай: значение гуляет вокруг сотни. Без запаса на снятие
        // это дало бы поток «упало / поднялось» каждые две минуты.
        var rule = new AlertRule { ClearMargin = 20, Cooldown = TimeSpan.Zero };

        var (state, events, _) = Run(
            rule,
            105, 106, // подъём
            99, 98,   // порог пройден, но без запаса — снятия нет
            101, 99,  // качание вокруг порога
            98, 102,
            99, 99);

        Assert.True(state.IsRaised);
        Assert.Equal([AlertAction.Raised], events);
    }

    [Fact(DisplayName = "Возврат с запасом алерт снимает")]
    public void MarginClears()
    {
        var rule = new AlertRule { ClearMargin = 20, Cooldown = TimeSpan.Zero };

        var (state, events, _) = Run(rule, 105, 106, 99, 98, 75, 74);

        Assert.False(state.IsRaised);
        Assert.Equal([AlertAction.Raised, AlertAction.Cleared], events);
    }

    [Fact(DisplayName = "Запас действует только после подъёма")]
    public void MarginAppliesOnlyWhenRaised()
    {
        // До подъёма порог один — 100. Значение 99 это норма, и требовать от него
        // запаса значило бы незаметно ужесточить порог, который задал человек.
        var rule = new AlertRule { ClearMargin = 20, Cooldown = TimeSpan.Zero };

        var (state, events, _) = Run(rule, 99, 99, 99);

        Assert.False(state.IsRaised);
        Assert.Empty(events);
        Assert.Equal(3, state.Good);
    }

    [Fact(DisplayName = "Пауза гасит оповещение, но не событие")]
    public void CooldownSilencesNotificationNotHistory()
    {
        var rule = new AlertRule { Cooldown = TimeSpan.FromHours(1) };

        // Подъём, снятие, снова подъём — всё внутри часа. Оповещение уходит один раз,
        // но история обязана сохранить все три события: иначе лента показывала бы
        // сеть спокойной ровно тогда, когда продукт решил не шуметь.
        var (_, events, notified) = Run(rule, 150, 150, 50, 50, 150, 150);

        Assert.Equal([AlertAction.Raised, AlertAction.Cleared, AlertAction.Raised], events);
        Assert.Equal([AlertAction.Raised], notified);
    }

    [Fact(DisplayName = "Порог подъёма можно поставить на предупреждение")]
    public void WarnCanTrigger()
    {
        var rule = new AlertRule { Trigger = VerdictLevel.Warn, RaiseAfter = 1, Cooldown = TimeSpan.Zero };

        var check = Check(50, Start) with { Level = VerdictLevel.Warn };
        var decision = AlertEvaluator.Apply(AlertState.Clear, check, Limit, rule, Start);

        Assert.Equal(AlertAction.Raised, decision.Action);
    }

    // ------------------------------------------------------------- ненаблюдение

    [Fact(DisplayName = "Пропущенная проверка не поднимает и не снимает алерт")]
    public void MissedCheckChangesNothing()
    {
        var raised = new AlertState { IsRaised = true, Bad = 5, RaisedUtc = Start };

        var missed = Check(50, Start) with { Kind = CheckKind.Missed, Level = VerdictLevel.Unknown };
        var decision = AlertEvaluator.Apply(raised, missed, Limit, new AlertRule(), Start);

        // О сети в это время не известно ничего. Снять алерт «за неимением
        // возражений» значило бы объявить сеть исправной по чужому сну.
        Assert.Equal(AlertAction.None, decision.Action);
        Assert.Same(raised, decision.State);
        Assert.True(decision.State.IsRaised);
        Assert.Equal(5, decision.State.Bad);
    }

    [Fact(DisplayName = "Обслуживание не поднимает и не снимает алерт")]
    public void MaintenanceChangesNothing()
    {
        var raised = new AlertState { IsRaised = true, Bad = 3, RaisedUtc = Start };

        var window = Check(50, Start) with { Kind = CheckKind.Maintenance, Level = VerdictLevel.Unknown };
        var decision = AlertEvaluator.Apply(raised, window, Limit, new AlertRule(), Start);

        Assert.Equal(AlertAction.None, decision.Action);
        Assert.True(decision.State.IsRaised);
        Assert.Contains("Обслуживание", decision.Reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ повторы

    [Fact(DisplayName = "Без настройки повтора продукт не напоминает")]
    public void NoRepeatByDefault()
    {
        var (_, events, _) = Run(new AlertRule { Cooldown = TimeSpan.Zero }, 150, 150, 150, 150, 150);

        Assert.Equal([AlertAction.Raised], events);
    }

    [Fact(DisplayName = "Настроенный повтор напоминает через заданный срок")]
    public void RepeatReminds()
    {
        var rule = new AlertRule
        {
            Cooldown = TimeSpan.Zero,
            RepeatEvery = TimeSpan.FromMinutes(3),
        };

        // Проверки идут раз в минуту: подъём, потом напоминание на третьей минуте
        // после него, потом ещё через три.
        var (_, events, _) = Run(rule, 150, 150, 150, 150, 150, 150, 150, 150);

        Assert.Equal(
            [AlertAction.Raised, AlertAction.Repeated, AlertAction.Repeated],
            events);
    }

    [Fact(DisplayName = "Повтор чаще паузы отвергается проверкой")]
    public void RepeatFasterThanCooldownRejected()
    {
        var errors = new AlertRule
        {
            Cooldown = TimeSpan.FromMinutes(15),
            RepeatEvery = TimeSpan.FromMinutes(5),
        }.Validate();

        Assert.Single(errors);
        Assert.Contains("пауза всё равно не даст ему сработать", errors[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Порог сужается в правильную сторону")]
    public void TightenGoesTheRightWay()
    {
        Assert.Equal(80, Threshold.Parse("p95 < 100").Tighten(20).Value);
        Assert.Equal(80, Threshold.Parse("p95 <= 100").Tighten(20).Value);
        Assert.Equal(120, Threshold.Parse("Осталось дней >= 100").Tighten(20).Value);
        Assert.Equal(120, Threshold.Parse("Осталось дней > 100").Tighten(20).Value);

        // Отрицательный запас не расширяет допустимое: он бы делал снятие легче
        // подъёма и превращал гистерезис в его противоположность.
        Assert.Equal(80, Threshold.Parse("p95 < 100").Tighten(-20).Value);
    }
}
