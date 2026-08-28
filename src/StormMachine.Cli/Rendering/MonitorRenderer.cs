using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ мониторов, доступности и алертов.
/// </summary>
/// <remarks>
/// Главная забота здесь — не дать прочитать числа выгоднее, чем они есть. Доступность
/// показывается вместе с покрытием, простой — вместе с точностью его границ, а цель
/// SLA — вместе с остатком бюджета ошибок. Одна цифра «99.8%» без этого окружения
/// сообщает уверенность, которой у измерения нет.
/// </remarks>
internal static class MonitorRenderer
{
    public static void WriteList(IReadOnlyList<(Monitor Monitor, MonitorStatus Status)> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (monitors.Count == 0)
        {
            Console.WriteLine("Мониторов нет.");
            Console.WriteLine();
            Console.WriteLine("Завести первый:");
            Console.WriteLine("  storm monitors add шлюз --проба ping --цель 192.168.1.1 \\");
            Console.WriteLine("      --каждые 1м --порог \"loss < 1\" --алерт --канал webhook");

            return;
        }

        Console.WriteLine($"  {"монитор",-20} {"состояние",-12} {"проверка",-26} {"расписание",-22} следующая");

        foreach (var (monitor, status) in monitors)
        {
            var state = monitor.IsEnabled ? StateText(status.Level) : "выключен";
            var last = status.LastRunUtc is { } at
                ? at.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                : "не запускался";

            var next = !monitor.IsEnabled
                ? "—"
                : monitor.NextDueUtc is { } due
                    ? due.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                    : "не назначена";

            Console.WriteLine(
                $"  {Cut(monitor.Name, 20),-20} {state,-12} {last,-26} "
                + $"{Cut(monitor.Schedule.Describe(), 22),-22} {next}");

            if (status.Alert.IsRaised)
            {
                Console.WriteLine($"  {string.Empty,-20} ! алерт поднят{Since(status.Alert.RaisedUtc)}");
            }

            if (!string.IsNullOrWhiteSpace(status.LastSummary))
            {
                Console.WriteLine($"  {string.Empty,-20} {status.LastSummary}");
            }
        }

        Console.WriteLine();
    }

    public static void WriteDetails(Monitor monitor, MonitorStatus status)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(status);

        Console.WriteLine($"Монитор «{monitor.Name}»{(monitor.IsEnabled ? string.Empty : "  (выключен)")}");

        if (!string.IsNullOrWhiteSpace(monitor.Description))
        {
            Console.WriteLine($"  {monitor.Description}");
        }

        Console.WriteLine();
        Console.WriteLine($"  что запускает : {(monitor.Kind == MonitorKind.Scenario ? "сценарий" : "проба")} "
                          + $"«{monitor.Subject}» на {monitor.Target.DisplayName}");

        if (monitor.Parameters.Count > 0)
        {
            Console.WriteLine("  параметры     : "
                              + string.Join(", ", monitor.Parameters.Select(p => $"{p.Key}={p.Value}")));
        }

        Console.WriteLine($"  расписание    : {monitor.Schedule.Describe()}");
        Console.WriteLine($"  пропуски      : {MisfireText(monitor.Schedule.Misfire)}");

        foreach (var window in monitor.Schedule.Maintenance)
        {
            Console.WriteLine($"  обслуживание  : {window.Describe()}");
        }

        Console.WriteLine(monitor.Thresholds.Count == 0
            ? "  пороги        : не заданы — монитор собирает историю, но ни о чём не судит"
            : "  пороги        : " + string.Join(", ", monitor.Thresholds.Select(t => $"{t.Describe()} ({LevelWord(t.Level)})")));

        Console.WriteLine(monitor.Alert is { } rule
            ? $"  алерт         : {rule.Describe()}"
            : "  алерт         : не задан — монитор молчит");

        if (monitor.Alert is { Channels.Count: > 0 } withChannels)
        {
            Console.WriteLine($"  каналы        : {string.Join(", ", withChannels.Channels)}");
        }

        if (monitor.Objective is { } objective)
        {
            Console.WriteLine($"  цель SLA      : {objective.Describe()}");
        }

        Console.WriteLine();
        Console.WriteLine($"  состояние     : {StateText(status.Level)}");

        if (!string.IsNullOrWhiteSpace(status.LastSummary))
        {
            Console.WriteLine($"  последнее     : {status.LastSummary}");
        }

        if (status.Alert.IsRaised)
        {
            Console.WriteLine($"  алерт поднят  : {Local(status.Alert.RaisedUtc)}{Since(status.Alert.RaisedUtc)}");
        }

        Console.WriteLine($"  следующая     : {(monitor.NextDueUtc is { } due ? Local(due) : "не назначена")}");
        Console.WriteLine();
    }

    public static void WriteCheck(MonitorCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var mark = check.Kind switch
        {
            CheckKind.Maintenance => "· ",
            CheckKind.Missed => "? ",
            _ => check.Level switch
            {
                VerdictLevel.Fail => "! ",
                VerdictLevel.Warn => "~ ",
                _ => "  ",
            },
        };

        Console.WriteLine($"{mark}{Local(check.StartedUtc)}  {StateText(check.Level),-12} {check.Summary}");

        if (check.Metric is { } metric && check.Value is { } value)
        {
            var threshold = check.Threshold is { } limit
                ? $", порог {limit.ToString("0.###", CultureInfo.InvariantCulture)}"
                : string.Empty;

            Console.WriteLine($"    {metric} = {value.ToString("0.###", CultureInfo.InvariantCulture)}{threshold}");
        }

        if (!string.IsNullOrWhiteSpace(check.Error))
        {
            Console.WriteLine($"    {check.Error}");
        }
    }

    public static void WriteAvailability(Monitor monitor, Availability availability)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(availability);

        Console.WriteLine($"Доступность «{monitor.Name}» "
                          + $"с {Local(availability.FromUtc)} по {Local(availability.ToUtc)}");
        Console.WriteLine();

        if (availability.Total == 0)
        {
            Console.WriteLine("  За этот период не было ни одного наблюдения.");
            Console.WriteLine("  Числа считать не из чего — это не «100%», а отсутствие данных.");

            return;
        }

        Console.WriteLine($"  доступность   : {Percent(availability.UptimePercent)} "
                          + $"от наблюдавшегося времени ({Schedule.Elapsed(availability.Observed)})");

        // Покрытие идёт следом за доступностью намеренно: это первое, что делает
        // её осмысленной или бессмысленной.
        Console.WriteLine($"  покрытие      : {Percent(availability.Coverage * 100)} окна"
                          + Coverage(availability));

        Console.WriteLine($"  проверок      : {availability.Total} "
                          + $"(норма {availability.Ok}, предупреждений {availability.Warn}, отказов {availability.Fail})");

        Console.WriteLine($"  простой       : {Schedule.Elapsed(availability.Down)}"
                          + (availability.Resolution > TimeSpan.Zero
                              ? $" (границы известны с точностью до {Schedule.Elapsed(availability.Resolution)} — "
                                + "состояние видно только в моменты проверок)"
                              : string.Empty));

        if (availability.Maintenance > TimeSpan.Zero)
        {
            Console.WriteLine($"  обслуживание  : {Schedule.Elapsed(availability.Maintenance)} — исключено из расчёта");
        }

        if (availability.Unobserved > TimeSpan.Zero)
        {
            Console.WriteLine($"  не наблюдали  : {Schedule.Elapsed(availability.Unobserved)} — "
                              + "продукт не работал, о сети в это время данных нет");
        }

        Console.WriteLine();
        Console.WriteLine($"  инцидентов    : {availability.Incidents.Count}");

        if (availability.MeanTimeBetweenFailures is { } mtbf)
        {
            Console.WriteLine($"  наработка     : {Schedule.Elapsed(mtbf)} между отказами");
        }

        Console.WriteLine(availability.MeanTimeToRecovery is { } mttr
            ? $"  восстановление: {Schedule.Elapsed(mttr)} в среднем"
            : "  восстановление: считать не по чему — завершённых инцидентов не было");

        foreach (var incident in availability.Incidents.Take(10))
        {
            var tail = incident.IsOpen ? "идёт сейчас" : Schedule.Elapsed(incident.Duration);

            Console.WriteLine($"    {Local(incident.StartedUtc)}  {tail,-14} {incident.Summary}");
        }

        if (availability.Objective is { } objective)
        {
            WriteObjective(availability, objective);
        }

        Console.WriteLine();
    }

    public static void WriteAlerts(IReadOnlyList<AlertEvent> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        if (alerts.Count == 0)
        {
            Console.WriteLine("Событий нет.");

            return;
        }

        foreach (var alert in alerts)
        {
            var mark = alert.Action switch
            {
                AlertAction.Raised => "!",
                AlertAction.Cleared => "+",
                _ => "~",
            };

            Console.WriteLine($"{mark} {Local(alert.AtUtc)}  {alert.MonitorName,-20} {alert.ActionText}");
            Console.WriteLine($"    {alert.Reason}");

            if (!string.IsNullOrWhiteSpace(alert.Summary))
            {
                Console.WriteLine($"    {alert.Summary}");
            }

            // «Событие было, шуметь не стали» — это состояние продукта, а не пробел
            // в истории, и читателю ленты его надо видеть.
            Console.WriteLine($"    {Delivery(alert)}");

            foreach (var error in alert.DeliveryErrors)
            {
                Console.WriteLine($"    не доставлено — {error}");
            }
        }

        Console.WriteLine();
    }

    public static void WriteChannels(IReadOnlyList<IAlertChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        Console.WriteLine($"  {"канал",-14} {"состояние",-12} что это");

        foreach (var channel in channels)
        {
            Console.WriteLine($"  {channel.Name,-14} {(channel.IsConfigured ? "настроен" : "не настроен"),-12} {channel.Title}");

            if (!channel.IsConfigured)
            {
                Console.WriteLine($"  {string.Empty,-14} {channel.MissingConfiguration}");
            }
        }

        Console.WriteLine();
    }

    public static void WriteMisfires(IReadOnlyList<MisfireReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        if (reports.Count == 0)
        {
            return;
        }

        Console.WriteLine("Пока продукт не работал, прошли назначенные сроки:");

        foreach (var report in reports)
        {
            Console.WriteLine($"  {report.Monitor.Name,-20} пропущено {report.Missed,4} — {report.Action}");
        }

        Console.WriteLine();
    }

    private static void WriteObjective(Availability availability, ServiceLevelObjective objective)
    {
        Console.WriteLine();
        Console.WriteLine($"  цель          : {objective.Describe()}");

        var verdict = availability.IsMet switch
        {
            true => "выполняется",
            false => "НАРУШЕНА",
            _ => "оценить не по чему",
        };

        Console.WriteLine($"  итог          : {verdict}");

        if (availability.ErrorBudget is { } budget)
        {
            var left = availability.ErrorBudgetLeft ?? TimeSpan.Zero;
            var used = availability.ErrorBudgetUsedPercent ?? 0;

            Console.WriteLine($"  бюджет ошибок : {Schedule.Elapsed(budget)} допустимо, "
                              + $"израсходовано {Percent(used)}, осталось {Schedule.Elapsed(left)}");
        }

        // Цель за месяц, посчитанная по трём дням наблюдений, — не выполненная цель,
        // а обещание, которое ещё нечем подтвердить.
        if (availability.Coverage < 0.9)
        {
            Console.WriteLine("  оговорка      : окно наблюдалось не полностью, "
                              + "и вывод по цели предварителен");
        }
    }

    /// <summary>
    /// Что стало с оповещением.
    /// </summary>
    /// <remarks>
    /// Пустой список каналов и «каналы не заданы» — разные вещи. Правило было задано
    /// с каналами, но ни один из них не доставил: сказать здесь «не заданы» значило бы
    /// свалить чужую неисправность на настройку.
    /// </remarks>
    private static string Delivery(AlertEvent alert)
    {
        if (!alert.Notified)
        {
            return "не оповещали: пауза между сообщениями ещё не истекла";
        }

        if (alert.Channels.Count > 0)
        {
            return "каналы: " + string.Join(", ", alert.Channels);
        }

        return alert.DeliveryErrors.Count > 0
            ? "ни один канал не доставил"
            : "оповещать было некуда — каналы в правиле не заданы";
    }

    private static string Coverage(Availability availability) => availability.Coverage switch
    {
        >= 0.95 => string.Empty,
        >= 0.5 => "  — часть окна не наблюдалась",
        _ => "  — данных мало, доверять числу выше нельзя",
    };

    private static string StateText(VerdictLevel level) => level switch
    {
        VerdictLevel.Pass => "норма",
        VerdictLevel.Warn => "предупреждение",
        VerdictLevel.Fail => "отказ",
        _ => "неизвестно",
    };

    private static string LevelWord(VerdictLevel level) => level switch
    {
        VerdictLevel.Warn => "предупреждение",
        VerdictLevel.Fail => "отказ",
        _ => "нет",
    };

    private static string MisfireText(MisfirePolicy policy) => policy switch
    {
        MisfirePolicy.RunOnce => "после простоя выполнить один раз, дальше по расписанию",
        _ => "после простоя пропустить, ждать следующего срока",
    };

    private static string Percent(double value) =>
        value.ToString(value >= 99.9 ? "0.###" : "0.##", CultureInfo.InvariantCulture) + "%";

    private static string Local(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Local(DateTimeOffset? moment) => moment is { } value ? Local(value) : "неизвестно";

    private static string Since(DateTimeOffset? moment) =>
        moment is { } value ? $" ({Schedule.Elapsed(DateTimeOffset.UtcNow - value)} назад)" : string.Empty;

    private static string Cut(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";
}
