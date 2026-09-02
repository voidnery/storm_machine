using System.Globalization;
using StormMachine.Domain.Results;

namespace StormMachine.Domain.Monitors;

/// <summary>Цель по доступности: сколько процентов за какое окно.</summary>
public sealed record ServiceLevelObjective
{
    /// <summary>Требуемая доступность в процентах: 99.5, 99.9, 99.99.</summary>
    public required double TargetPercent { get; init; }

    /// <summary>За какой срок она считается.</summary>
    public required TimeSpan Window { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (TargetPercent is <= 0 or > 100)
        {
            errors.Add("Цель по доступности задаётся процентом от нуля до ста.");
        }

        if (Window <= TimeSpan.Zero)
        {
            errors.Add("Окно расчёта доступности должно быть положительным.");
        }

        return errors;
    }

    /// <summary>Сколько простоя допускает цель за всё окно.</summary>
    public TimeSpan BudgetFor(TimeSpan observed) =>
        observed <= TimeSpan.Zero ? TimeSpan.Zero : observed * ((100 - TargetPercent) / 100);

    public string Describe() =>
        $"{TargetPercent.ToString("0.###", CultureInfo.InvariantCulture)}% за {Schedule.Elapsed(Window)}";
}

/// <summary>Один непрерывный простой.</summary>
public sealed record AvailabilityIncident
{
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Когда восстановилось. Пусто — идёт прямо сейчас.</summary>
    public DateTimeOffset? EndedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    public required int Checks { get; init; }

    public required string Summary { get; init; }

    public bool IsOpen => EndedUtc is null;
}

/// <summary>
/// Доступность за период.
/// </summary>
/// <remarks>
/// Числа здесь устроены так, чтобы их нельзя было прочитать выгоднее, чем есть.
/// <para>
/// <b>Доступность считается по времени, а не по числу проверок.</b> «99 из 100 проверок
/// прошли» и «недоступно 1% времени» совпадают только при ровном интервале, а монитор
/// с cron-расписанием ровным не бывает.
/// </para>
/// <para>
/// <b>Ненаблюдавшееся время в знаменатель не входит</b> — ни как работа, ни как простой.
/// Пока продукт был выключен, о сети не известно ничего, и любая из двух подстановок
/// была бы выдумкой. Вместо этого есть <see cref="Coverage"/>: доля окна, которую мы
/// действительно видели. Доступность 100% при покрытии 4% — это не отличная сеть,
/// а отсутствие данных, и цифры обязаны показывать разницу.
/// </para>
/// </remarks>
public sealed record Availability
{
    public required DateTimeOffset FromUtc { get; init; }

    public required DateTimeOffset ToUtc { get; init; }

    public int Total { get; init; }

    public int Ok { get; init; }

    public int Warn { get; init; }

    public int Fail { get; init; }

    /// <summary>Время, о котором есть данные.</summary>
    public TimeSpan Observed { get; init; }

    /// <summary>Из наблюдавшегося — время в отказе.</summary>
    public TimeSpan Down { get; init; }

    /// <summary>Время плановых работ. Из знаменателя исключено.</summary>
    public TimeSpan Maintenance { get; init; }

    /// <summary>Время, когда продукт не работал и ничего не видел.</summary>
    public TimeSpan Unobserved { get; init; }

    public IReadOnlyList<AvailabilityIncident> Incidents { get; init; } = [];

    /// <summary>Средняя наработка между отказами.</summary>
    public TimeSpan? MeanTimeBetweenFailures { get; init; }

    /// <summary>Среднее время восстановления.</summary>
    public TimeSpan? MeanTimeToRecovery { get; init; }

    public ServiceLevelObjective? Objective { get; init; }

    /// <summary>Доступность в процентах от наблюдавшегося времени.</summary>
    public double UptimePercent => Observed <= TimeSpan.Zero
        ? 0
        : (Observed - Down) / Observed * 100;

    /// <summary>Какую долю окна мы действительно видели.</summary>
    public double Coverage
    {
        get
        {
            var span = ToUtc - FromUtc - Maintenance;

            return span <= TimeSpan.Zero ? 0 : Math.Clamp(Observed / span, 0, 1);
        }
    }

    /// <summary>Допустимый простой по цели.</summary>
    public TimeSpan? ErrorBudget => Objective?.BudgetFor(Observed);

    public TimeSpan? ErrorBudgetLeft => ErrorBudget is { } budget
        ? budget - Down < TimeSpan.Zero ? TimeSpan.Zero : budget - Down
        : null;

    public double? ErrorBudgetUsedPercent => ErrorBudget is { } budget && budget > TimeSpan.Zero
        ? Down / budget * 100
        : null;

    public bool? IsMet => Objective is { } objective && Observed > TimeSpan.Zero
        ? UptimePercent >= objective.TargetPercent
        : null;

    /// <summary>
    /// Насколько точны границы простоя.
    /// </summary>
    /// <remarks>
    /// Состояние известно только в моменты проверок. Отказ, начавшийся сразу после
    /// удачной проверки, будет замечен на следующей — то есть с точностью до интервала.
    /// Число нужно рядом с длительностью простоя: «14 минут ± 5» и «14 минут» —
    /// разные утверждения.
    /// </remarks>
    public TimeSpan Resolution { get; init; }

    /// <summary>
    /// Достаточно ли наблюдалось окно, чтобы верить числам выше.
    /// </summary>
    /// <remarks>
    /// Порог был написан трижды и разошёлся: консоль ставила пометку у самого числа
    /// с 0.95, а оговорку под ним — с 0.9; окно и отчёт знали только про 0.9.
    /// При покрытии 0.92 продукт говорил рядом «часть окна не наблюдалась» и тут же
    /// молчал в оговорке — два ответа на один вопрос. Порог один и живёт здесь.
    /// </remarks>
    public const double TrustedCoverage = 0.9;

    /// <summary>Ниже этого покрытия числу нельзя верить вовсе, а не «отчасти».</summary>
    public const double UsableCoverage = 0.5;

    /// <summary>Наблюдалось ли окно достаточно, чтобы вывод по цели был окончательным.</summary>
    public bool IsWellObserved => Coverage >= TrustedCoverage;

    /// <summary>
    /// Оговорка про покрытие. Пусто — окно наблюдалось достаточно.
    /// </summary>
    /// <remarks>
    /// Доступность 100% при покрытии 4% — это отсутствие данных, а не отличная сеть,
    /// и разницу обязан называть сам продукт, одинаково во всех трёх местах показа.
    /// </remarks>
    public string? CoverageNotice => Total == 0
        ? "За период нет ни одного наблюдения — считать не из чего."
        : Coverage >= TrustedCoverage
            ? null
            : Coverage >= UsableCoverage
                ? "Окно наблюдалось не полностью, и вывод по цели предварителен: "
                  + "часть периода продукт не работал и о сети в это время данных нет."
                : "Окно наблюдалось меньше чем наполовину — доверять числам выше нельзя.";
}

/// <summary>Расчёт доступности по журналу проверок.</summary>
public static class AvailabilityCalculator
{
    /// <summary>
    /// Считает доступность за окно.
    /// </summary>
    /// <param name="checks">Проверки, любые по порядку — будут отсортированы.</param>
    /// <param name="from">Начало окна.</param>
    /// <param name="to">Конец окна: обычно «сейчас».</param>
    /// <param name="objective">Цель, если задана.</param>
    public static Availability Compute(
        IReadOnlyList<MonitorCheck> checks,
        DateTimeOffset from,
        DateTimeOffset to,
        ServiceLevelObjective? objective = null)
    {
        ArgumentNullException.ThrowIfNull(checks);

        var ordered = checks
            .Where(c => c.StartedUtc >= from && c.StartedUtc <= to)
            .OrderBy(c => c.StartedUtc)
            .ToList();

        if (ordered.Count == 0)
        {
            return new Availability
            {
                FromUtc = from,
                ToUtc = to,
                Unobserved = to - from,
                Objective = objective,
            };
        }

        var observed = TimeSpan.Zero;
        var down = TimeSpan.Zero;
        var maintenance = TimeSpan.Zero;
        var unobserved = TimeSpan.Zero;
        var intervals = new List<TimeSpan>();
        var incidents = new List<AvailabilityIncident>();

        DateTimeOffset? failStart = null;
        var failChecks = 0;
        var failSummary = string.Empty;

        // Время до первой проверки наблюдением не было: продукт мог быть выключен.
        unobserved += ordered[0].StartedUtc - from;

        for (var i = 0; i < ordered.Count; i++)
        {
            var check = ordered[i];
            var until = i + 1 < ordered.Count ? ordered[i + 1].StartedUtc : to;
            var span = until - check.StartedUtc;

            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            switch (check.Kind)
            {
                case CheckKind.Maintenance:
                    maintenance += span;

                    break;

                case CheckKind.Missed:
                    unobserved += span;

                    break;

                default:
                    observed += span;
                    intervals.Add(span);

                    if (check.Level == VerdictLevel.Fail)
                    {
                        down += span;
                        failStart ??= check.StartedUtc;
                        failChecks++;
                        failSummary = check.Summary;
                    }

                    break;
            }

            // Инцидент закрывается всем, что не отказ: и нормой, и обслуживанием,
            // и пропуском. Тянуть простой через время, которого мы не видели,
            // значило бы записать в отказ чужой сон.
            var stillDown = check.Kind == CheckKind.Measured && check.Level == VerdictLevel.Fail;

            if (!stillDown && failStart is { } started)
            {
                incidents.Add(Close(started, check.StartedUtc, failChecks, failSummary, closed: true));
                failStart = null;
                failChecks = 0;
            }
        }

        if (failStart is { } open)
        {
            incidents.Add(Close(open, to, failChecks, failSummary, closed: false));
        }

        var measured = ordered.Where(c => c.Kind == CheckKind.Measured).ToList();
        var closedIncidents = incidents.Where(i => !i.IsOpen).ToList();

        return new Availability
        {
            FromUtc = from,
            ToUtc = to,
            Total = measured.Count,
            Ok = measured.Count(c => c.Level == VerdictLevel.Pass),
            Warn = measured.Count(c => c.Level == VerdictLevel.Warn),
            Fail = measured.Count(c => c.Level == VerdictLevel.Fail),
            Observed = observed,
            Down = down,
            Maintenance = maintenance,
            Unobserved = unobserved,
            Incidents = incidents,

            // Наработка между отказами — наблюдавшееся исправное время, делённое
            // на число отказов. Один отказ за период даёт наработку, равную периоду:
            // это верно и означает лишь, что данных для среднего пока мало.
            MeanTimeBetweenFailures = incidents.Count > 0
                ? (observed - down) / incidents.Count
                : null,

            // Восстановление считается только по закрытым инцидентам: длительность
            // идущего прямо сейчас ещё неизвестна, и включать её в среднее — занижать.
            MeanTimeToRecovery = closedIncidents.Count > 0
                ? TimeSpan.FromTicks(closedIncidents.Sum(i => i.Duration.Ticks) / closedIncidents.Count)
                : null,

            Objective = objective,
            Resolution = intervals.Count > 0 ? Median(intervals) : TimeSpan.Zero,
        };
    }

    private static AvailabilityIncident Close(
        DateTimeOffset started,
        DateTimeOffset ended,
        int checks,
        string summary,
        bool closed) => new()
        {
            StartedUtc = started,
            EndedUtc = closed ? ended : null,
            Duration = ended - started,
            Checks = checks,
            Summary = summary,
        };

    private static TimeSpan Median(List<TimeSpan> values)
    {
        values.Sort();

        return values[values.Count / 2];
    }
}

/// <summary>
/// Сравнение доступности за два соседних периода.
/// </summary>
/// <remarks>
/// Закрывает долг И-15: эталон снимался с прогона, а сравнить «доступность за этот месяц
/// против прошлого» было нечем — данные для этого есть с И-14, механики не было.
/// <para>
/// Сравнивать доступность напрямую нельзя, и в этом вся тонкость. Доступность считается
/// от <b>наблюдавшегося</b> времени, поэтому 100 % при покрытии 4 % — это не отличный
/// месяц, а месяц, который мы почти не смотрели. Сравнение таких двух чисел даёт
/// правдоподобный и бессмысленный ответ, и продукт обязан сказать об этом раньше,
/// чем оператор сделает вывод.
/// </para>
/// </remarks>
public sealed record AvailabilityComparison
{
    public required Availability Before { get; init; }

    public required Availability After { get; init; }

    /// <summary>Насколько изменилась доступность, в процентных пунктах.</summary>
    public double DeltaPercent => After.UptimePercent - Before.UptimePercent;

    /// <summary>Насколько изменилось время простоя.</summary>
    public TimeSpan DeltaDown => After.Down - Before.Down;

    /// <summary>Изменилось ли число инцидентов.</summary>
    public int DeltaIncidents => After.Incidents.Count - Before.Incidents.Count;

    /// <summary>
    /// Можно ли вообще сравнивать эти два периода.
    /// </summary>
    /// <remarks>
    /// Порог покрытия — половина. Ниже неё период наблюдался меньше, чем не наблюдался,
    /// и его доступность говорит скорее о работе продукта, чем о работе сети.
    /// </remarks>
    public bool IsComparable => Before.Coverage >= 0.5 && After.Coverage >= 0.5;

    /// <summary>Почему сравнивать нельзя. <c>null</c> — можно.</summary>
    public string? Caveat
    {
        get
        {
            if (IsComparable)
            {
                return null;
            }

            var poor = Before.Coverage < After.Coverage ? Before : After;
            var which = ReferenceEquals(poor, Before) ? "прошлый" : "этот";

            return $"{which} период наблюдался лишь на "
                   + $"{(poor.Coverage * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} % — "
                   + "сравнивать доступность с таким покрытием нельзя: она посчитана "
                   + "по тому немногому, что видели.";
        }
    }

    /// <summary>
    /// Что изменилось, человеческим языком.
    /// </summary>
    /// <remarks>
    /// Порог в одну сотую процентного пункта — не косметика. Доступность считается
    /// из времени, и два периода не совпадают до нуля почти никогда; называть
    /// изменением разницу в тысячные значило бы сообщать шум как новость.
    /// </remarks>
    public string Describe()
    {
        if (Caveat is { } caveat)
        {
            return caveat;
        }

        var delta = DeltaPercent;

        if (Math.Abs(delta) < 0.01)
        {
            return "Доступность не изменилась.";
        }

        var direction = delta > 0 ? "выросла" : "упала";
        var value = Math.Abs(delta).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        var incidents = DeltaIncidents switch
        {
            0 => "число инцидентов то же",
            > 0 => $"инцидентов больше на {DeltaIncidents.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            _ => $"инцидентов меньше на {(-DeltaIncidents).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        };

        return $"Доступность {direction} на {value} процентного пункта; {incidents}.";
    }
}
