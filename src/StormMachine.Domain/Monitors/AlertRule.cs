using System.Globalization;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Domain.Monitors;

/// <summary>
/// Правило оповещения с гистерезисом.
/// </summary>
/// <remarks>
/// Задача правила — не «сравнить с порогом»: это уже сделал вердикт. Задача —
/// решить, когда об этом стоит будить человека. Метрика, гуляющая вокруг порога,
/// при наивном сравнении даёт поток «упало / поднялось / упало» каждые полминуты,
/// после которого оповещения перестают читать вовсе.
/// <para>
/// Дребезг гасится тремя независимыми средствами, и они нужны все три, потому что
/// закрывают разные случаи:
/// </para>
/// <list type="number">
/// <item><b>Счётчики подряд.</b> Одиночный выброс не поднимает алерт, одиночная
/// удача его не снимает. Лечит короткие всплески.</item>
/// <item><b>Запас на снятие</b> (<see cref="ClearMargin"/>). Пока алерт поднят,
/// возврат к норме засчитывается только с запасом: подняли на 100 мс — снимем на 80.
/// Это классический триггер Шмитта. Лечит метрику, севшую ровно на пороге.</item>
/// <item><b>Пауза между оповещениями</b> (<see cref="Cooldown"/>). Даже если состояние
/// честно сменилось, канал не дёргается чаще заданного. Лечит долгое качание.</item>
/// </list>
/// </remarks>
public sealed record AlertRule
{
    /// <summary>Какой вердикт считать поводом. По умолчанию только отказ.</summary>
    public VerdictLevel Trigger { get; init; } = VerdictLevel.Fail;

    /// <summary>Сколько нарушений подряд поднимают алерт.</summary>
    public int RaiseAfter { get; init; } = 2;

    /// <summary>Сколько нормальных проверок подряд его снимают.</summary>
    public int ClearAfter { get; init; } = 2;

    /// <summary>
    /// Запас по метрике, с которым засчитывается возврат к норме.
    /// </summary>
    /// <remarks>
    /// В единицах самой метрики. Действует только пока алерт поднят: до подъёма
    /// порог один, после подъёма снятие требует пройти его с запасом.
    /// </remarks>
    public double? ClearMargin { get; init; }

    /// <summary>Не оповещать чаще, чем раз в этот срок.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Повторять оповещение, пока алерт держится.
    /// </summary>
    /// <remarks>
    /// По умолчанию не повторять. Повтор — это осознанный выбор в пользу «не забыть»
    /// против «не надоесть», и делать такой выбор за оператора молча нельзя.
    /// </remarks>
    public TimeSpan? RepeatEvery { get; init; }

    /// <summary>Сообщать ли о возврате к норме.</summary>
    public bool NotifyOnClear { get; init; } = true;

    /// <summary>Имена каналов: <c>журнал</c>, <c>звук</c>, <c>webhook</c>, <c>почта</c>.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Trigger is VerdictLevel.Unknown or VerdictLevel.Pass)
        {
            errors.Add("Поводом для алерта может быть предупреждение или отказ, но не успех.");
        }

        if (RaiseAfter < 1)
        {
            errors.Add("Число нарушений для подъёма — не меньше одного.");
        }

        if (ClearAfter < 1)
        {
            errors.Add("Число нормальных проверок для снятия — не меньше одной.");
        }

        if (ClearMargin is < 0)
        {
            errors.Add("Запас на снятие не может быть отрицательным: он бы расширял нарушение, а не сужал.");
        }

        if (Cooldown < TimeSpan.Zero)
        {
            errors.Add("Пауза между оповещениями не может быть отрицательной.");
        }

        if (RepeatEvery is { } repeat && repeat < Cooldown)
        {
            errors.Add(
                $"Повтор ({Schedule.Humanize(repeat)}) чаще паузы между оповещениями "
                + $"({Schedule.Humanize(Cooldown)}) — пауза всё равно не даст ему сработать.");
        }

        return errors;
    }

    public string Describe()
    {
        var parts = new List<string>
        {
            $"поднять после {RaiseAfter.ToString(CultureInfo.InvariantCulture)} подряд",
            $"снять после {ClearAfter.ToString(CultureInfo.InvariantCulture)} подряд",
        };

        if (ClearMargin is { } margin)
        {
            parts.Add($"запас на снятие {margin.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        parts.Add($"не чаще раза в {Schedule.Humanize(Cooldown)}");

        if (RepeatEvery is { } repeat)
        {
            parts.Add($"повтор раз в {Schedule.Humanize(repeat)}");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>Состояние оповещения между проверками.</summary>
public sealed record AlertState
{
    public static readonly AlertState Clear = new();

    public bool IsRaised { get; init; }

    /// <summary>Нарушений подряд к текущему моменту.</summary>
    public int Bad { get; init; }

    /// <summary>Нормальных проверок подряд к текущему моменту.</summary>
    public int Good { get; init; }

    public DateTimeOffset? RaisedUtc { get; init; }

    public DateTimeOffset? ClearedUtc { get; init; }

    /// <summary>Когда каналы оповещались в последний раз — этим и держится пауза.</summary>
    public DateTimeOffset? LastNotifiedUtc { get; init; }
}

/// <summary>Что случилось с алертом на этой проверке.</summary>
public enum AlertAction
{
    None,

    Raised,

    Cleared,

    /// <summary>Алерт держится, и настало время напомнить.</summary>
    Repeated,
}

/// <summary>Решение по одной проверке.</summary>
/// <param name="State">Новое состояние — его и сохраняем.</param>
/// <param name="Action">Что произошло. Записывается в историю всегда.</param>
/// <param name="Notify">Дёргать ли каналы. Пауза может погасить оповещение, не отменяя события.</param>
/// <param name="Reason">Объяснение человеческим языком — идёт и в ленту, и в сообщение канала.</param>
public sealed record AlertDecision(AlertState State, AlertAction Action, bool Notify, string Reason);

/// <summary>
/// Применение правила к очередной проверке.
/// </summary>
/// <remarks>
/// Чистая функция без часов и хранилища: состояние на входе, состояние на выходе.
/// Иначе поведение на границе порога нельзя было бы проверить тестом, а именно оно
/// и есть предмет приёмки этой итерации.
/// </remarks>
public static class AlertEvaluator
{
    public static AlertDecision Apply(
        AlertState state,
        MonitorCheck check,
        Threshold? trigger,
        AlertRule rule,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(rule);

        // Ненаблюдение — не событие. Пропущенная проверка не поднимает алерт (мы не знаем,
        // что было с сетью) и не снимает его (тем более не знаем). Поднять алерт на том,
        // что спала машина оператора, значило бы обвинить сеть в чужом сне.
        if (check.Kind != CheckKind.Measured)
        {
            return new AlertDecision(
                state,
                AlertAction.None,
                false,
                check.Kind == CheckKind.Maintenance
                    ? "Обслуживание — проверка не выполнялась."
                    : "Проверка пропущена — о сети в это время ничего не известно.");
        }

        var isBad = check.Level >= rule.Trigger && check.Level != VerdictLevel.Unknown;

        return isBad ? Bad(state, rule, now) : Good(state, check, trigger, rule, now);
    }

    private static AlertDecision Bad(AlertState state, AlertRule rule, DateTimeOffset now)
    {
        var next = state with { Bad = state.Bad + 1, Good = 0 };

        if (!next.IsRaised)
        {
            if (next.Bad < rule.RaiseAfter)
            {
                return new AlertDecision(
                    next,
                    AlertAction.None,
                    false,
                    $"Нарушение {Count(next.Bad)} из {Count(rule.RaiseAfter)} — жду подтверждения.");
            }

            var notify = Allowed(state.LastNotifiedUtc, rule.Cooldown, now);

            return new AlertDecision(
                next with
                {
                    IsRaised = true,
                    RaisedUtc = now,
                    ClearedUtc = null,
                    LastNotifiedUtc = notify ? now : state.LastNotifiedUtc,
                },
                AlertAction.Raised,
                notify,
                notify
                    ? $"Нарушение подтверждено {Count(rule.RaiseAfter)} проверками подряд."
                    : $"Нарушение подтверждено, но оповещение придержано: пауза {Schedule.Humanize(rule.Cooldown)} "
                      + "ещё не истекла.");
        }

        if (rule.RepeatEvery is { } repeat && Allowed(state.LastNotifiedUtc, repeat, now))
        {
            return new AlertDecision(
                next with { LastNotifiedUtc = now },
                AlertAction.Repeated,
                true,
                $"Держится с {Local(state.RaisedUtc)} — напоминание.");
        }

        return new AlertDecision(next, AlertAction.None, false, "Алерт уже поднят.");
    }

    private static AlertDecision Good(
        AlertState state,
        MonitorCheck check,
        Threshold? trigger,
        AlertRule rule,
        DateTimeOffset now)
    {
        if (!state.IsRaised)
        {
            return new AlertDecision(
                state with { Bad = 0, Good = state.Good + 1 },
                AlertAction.None,
                false,
                "В норме.");
        }

        // Мёртвая зона триггера Шмитта: порог пройден, но без запаса. Это не нарушение
        // и ещё не возврат — состояние держится как есть, и счётчики не двигаются.
        if (rule.ClearMargin is { } margin && trigger is not null && check.Value is { } value)
        {
            var tightened = trigger.Tighten(margin);

            if (!tightened.IsSatisfiedBy(value))
            {
                return new AlertDecision(
                    state,
                    AlertAction.None,
                    false,
                    $"Порог пройден, но без запаса: нужно {tightened.Describe()}, "
                    + $"а получено {value.ToString("0.###", CultureInfo.InvariantCulture)}.");
            }
        }

        var next = state with { Bad = 0, Good = state.Good + 1 };

        if (next.Good < rule.ClearAfter)
        {
            return new AlertDecision(
                next,
                AlertAction.None,
                false,
                $"Норма {Count(next.Good)} из {Count(rule.ClearAfter)} — жду подтверждения.");
        }

        var notify = rule.NotifyOnClear && Allowed(state.LastNotifiedUtc, rule.Cooldown, now);

        return new AlertDecision(
            next with
            {
                IsRaised = false,
                ClearedUtc = now,
                LastNotifiedUtc = notify ? now : state.LastNotifiedUtc,
            },
            AlertAction.Cleared,
            notify,
            $"Норма подтверждена {Count(rule.ClearAfter)} проверками подряд"
            + (state.RaisedUtc is { } raised ? $"; держалось {Schedule.Elapsed(now - raised)}." : "."));
    }

    private static bool Allowed(DateTimeOffset? last, TimeSpan pause, DateTimeOffset now) =>
        last is not { } moment || now - moment >= pause;

    private static string Local(DateTimeOffset? moment) =>
        moment is { } value ? value.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) : "неизвестно когда";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
