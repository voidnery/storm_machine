using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Text;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ сценария и его итога.
/// </summary>
/// <remarks>
/// Главное здесь — разбивка по фазам. Одно число «страница открылась за 460 мс»
/// не говорит, где потеряно время; строка на фазу говорит.
/// </remarks>
internal static class ScenarioRenderer
{
    public static void WriteTemplates()
    {
        Console.WriteLine("Готовые сценарии:");
        Console.WriteLine();

        foreach (var (key, title, about) in ScenarioTemplates.All)
        {
            Console.WriteLine($"  {key,-6} {title,-24} {about}");
        }

        Console.WriteLine();
        Console.WriteLine("Запуск: storm scenario run <шаблон> <цель>");
        Console.WriteLine("  например: storm scenario run web example.com");
    }

    public static void WriteTargetSets(INetworkEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        Console.WriteLine("Готовые наборы целей:");
        Console.WriteLine();

        foreach (var (key, title, about) in TargetSets.All)
        {
            var set = TargetSets.Resolve(key, environment);

            Console.WriteLine($"  {key,-12} {title,-22} {about}");
            Console.WriteLine($"  {string.Empty,-12} {set.Origin}: "
                              + (set.Targets.Count == 0 ? "пусто" : string.Join(", ", set.Targets)));
            Console.WriteLine();
        }

        Console.WriteLine("В поле цели принимается также список через запятую или @файл со списком.");
        Console.WriteLine("  например: storm scenario run web example.com,ya.ru");
        Console.WriteLine("            storm scenario run voice @цели.txt");
    }

    public static void WriteHeader(
        Scenario scenario,
        NetworkAdapter? adapter,
        IHighResolutionClock clock,
        TargetSet? set = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(clock);

        Console.WriteLine();
        Console.WriteLine($"Сценарий  : {scenario.Name}");

        if (scenario.Description is { Length: > 0 } description)
        {
            Console.WriteLine($"            {description}");
        }

        Console.WriteLine($"Шагов     : {scenario.Steps.Count}");

        if (set is not null && set.Targets.Count > 1)
        {
            Console.WriteLine($"Набор     : {set.Title} — {set.Origin}, целей {set.Targets.Count}");
        }

        Console.WriteLine($"Интерфейс : {adapter?.Name ?? "неизвестен"}"
                          + (adapter?.IPv4Address is { } ip ? $", {ip}" : string.Empty));
        Console.WriteLine($"Порог     : {clock.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс "
                          + "— ниже него измерения недостоверны");
        Console.WriteLine();
    }

    /// <summary>Живой вывод: строка на шаг по мере выполнения.</summary>
    public static Action<ScenarioProgress> CreateProgressWriter()
    {
        if (Console.IsOutputRedirected)
        {
            return progress =>
            {
                if (progress.Finished is { } finished)
                {
                    WriteStepLine(progress, finished);
                }
            };
        }

        return progress =>
        {
            if (progress.Finished is { } finished)
            {
                Console.Write('\r');
                Console.Write(new string(' ', 60));
                Console.Write('\r');

                WriteStepLine(progress, finished);
                return;
            }

            Console.Write($"\r  {progress.StepIndex + 1}/{progress.StepCount} {progress.StepName}…");
        };
    }

    private static void WriteStepLine(ScenarioProgress progress, ScenarioStepResult step)
    {
        // Измеренное, а не время шага: шаг длится столько, сколько задано числом проб
        // и паузой между ними, и рядом с вердиктом это число вводило бы в заблуждение.
        var value = step.PhaseMs is { } ms ? ProbeMetrics.Format("p50", ms) : string.Empty;

        Console.WriteLine($"  {VerdictWording.Mark(step.Verdict.Level)} {progress.StepIndex + 1}/{progress.StepCount} "
                          + $"{step.Name,-28} {value,9}   {step.Verdict.Summary}");

        if (step.Error is { Length: > 0 } error)
        {
            Console.WriteLine($"      {error}");
        }
    }

    /// <param name="baselineMs">
    /// Собственный порог часов. Ниже него измерение недостоверно, и показывать такое
    /// значение числом значило бы выдать шум измерителя за длительность фазы.
    /// </param>
    public static void WriteRun(ScenarioRun run, double baselineMs)
    {
        ArgumentNullException.ThrowIfNull(run);

        Console.WriteLine();
        Console.WriteLine($"--- {run.ScenarioName} ---");
        Console.WriteLine();

        WriteBreakdown(run, baselineMs);
        WriteVerdicts(run);
        WriteConclusion(run);
    }

    /// <summary>
    /// Разбивка по фазам.
    /// </summary>
    /// <remarks>
    /// То, ради чего сценарий и собирают. Число берётся из измерения, а не из часов
    /// вокруг шага: время шага задаётся числом проб и паузой между ними, и пять
    /// запросов с паузой 200 мс займут секунду независимо от того, отвечает сервер
    /// за 3 мс или за 300. Первая версия рисовала именно его — и отвечала на вопрос
    /// «где медленно» настройками замера.
    /// <para>
    /// Доля между шагами не считается намеренно. Шаги измеряют пересекающиеся отрезки:
    /// шаг «Страница» открывает собственное соединение и внутри себя заново разрешает
    /// имя и делает рукопожатие. Проценты от суммы означали бы двойной счёт. Внутри
    /// шага отрезки не пересекаются — там доля осмысленна, и там она показана.
    /// </para>
    /// </remarks>
    private static void WriteBreakdown(ScenarioRun run, double baselineMs)
    {
        var measured = run.Steps.Where(s => !s.WasSkipped).ToList();

        if (measured.Count == 0)
        {
            return;
        }

        var longest = measured.Select(s => s.PhaseMs ?? 0).DefaultIfEmpty(0).Max();

        Console.WriteLine($"  {"шаг",-30} {"измерено",9}");

        foreach (var step in run.Steps)
        {
            WriteBreakdownRow(step, longest, baselineMs);
        }

        Console.WriteLine();

        var metrics = measured
            .Select(s => s.PhaseMetric)
            .Where(m => m is { Length: > 0 })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (metrics.Count > 0)
        {
            Console.WriteLine($"  Измерено: {string.Join(", ", metrics)} по всем попыткам шага.");
        }

        if (measured.Count > 1)
        {
            // Без этой оговорки столбики читаются как слагаемые, а они не слагаемые.
            Console.WriteLine("  Шаги измеряют пересекающиеся отрезки: «Страница» включает в себя");
            Console.WriteLine("  и разрешение имени, и соединение, и рукопожатие. Столбики сравнимы");
            Console.WriteLine("  между собой, но не складываются — доля считается только внутри шага.");
        }

        Console.WriteLine($"  Проверка заняла {Seconds(run.Duration)} — это время сценария, а не время события.");
    }

    private static void WriteBreakdownRow(ScenarioStepResult step, double longest, double baselineMs)
    {
        if (step.WasSkipped)
        {
            Console.WriteLine($"  {step.Name,-30} {"—",9}  пропущен");
            return;
        }

        if (step.PhaseMs is not { } ms)
        {
            Console.WriteLine($"  {step.Name,-30} {"—",9}  измерить не удалось");
            return;
        }

        var bar = longest > 0 ? (int)Math.Round(ms / longest * 30) : 0;

        Console.WriteLine($"  {step.Name,-30} {Milliseconds(ms, baselineMs),9}  "
                          + new string('█', Math.Max(ms > 0 ? 1 : 0, bar)));

        WriteSeries(step, baselineMs);
    }

    private static void WriteSeries(ScenarioStepResult step, double baselineMs)
    {
        if (step.Series.Count < 2)
        {
            return;
        }

        if (step.Shape == ProbeResultShape.ComparedSeries)
        {
            WriteComparison(step, baselineMs);
            return;
        }

        WritePhases(step, baselineMs);
    }

    /// <summary>
    /// Водопад внутри шага.
    /// </summary>
    /// <remarks>
    /// Здесь и только здесь доля имеет смысл: фазы одного запроса идут подряд
    /// и складываются в него целиком. Именно эта таблица отвечает на вопрос,
    /// ради которого шаг и разложен: медленно в сети или на сервере.
    /// </remarks>
    private static void WritePhases(ScenarioStepResult step, double baselineMs)
    {
        var total = step.Series.Sum(p => p.Statistics.SampleCount > 0 ? p.Statistics.P50Ms : 0);

        foreach (var phase in step.Series)
        {
            if (phase.Statistics.SampleCount == 0)
            {
                Console.WriteLine($"      {phase.Label,-26} {"—",9}  не измерено, "
                                  + $"неудачных попыток: {phase.LostCount}");
                continue;
            }

            var value = phase.Statistics.P50Ms;
            var share = total > 0 ? value / total : 0;

            Console.WriteLine($"      {phase.Label,-26} {Milliseconds(value, baselineMs),9}  "
                              + $"{share.ToString("P0", CultureInfo.InvariantCulture),5}");
        }
    }

    /// <summary>
    /// Сравнение рядов внутри шага: пять резолверов в одной таблице.
    /// </summary>
    /// <remarks>
    /// Долей здесь нет и быть не может: ряды идут параллельно и ни во что не
    /// складываются. Осмысленно другое — порядок, поэтому строки идут от быстрого
    /// к медленному, а не в порядке перечисления в параметрах. Ради этого сравнение
    /// и делают: чтобы увидеть, кого стоит поставить резолвером по умолчанию.
    /// </remarks>
    private static void WriteComparison(ScenarioStepResult step, double baselineMs)
    {
        var answered = step.Series.Where(r => r.Statistics.SampleCount > 0).ToList();
        var ranked = answered.OrderBy(r => r.Statistics.P50Ms).ToList();

        foreach (var series in ranked)
        {
            var note = ranked.Count > 1 && ReferenceEquals(series, ranked[0])
                ? "быстрее всех"
                : ranked.Count > 1 && ReferenceEquals(series, ranked[^1])
                    ? "медленнее всех"
                    : string.Empty;

            if (series.LostCount > 0)
            {
                note = $"потеряно {series.LostCount} из {series.SentCount}";
            }

            Console.WriteLine($"      {series.Label,-26} "
                              + $"{Milliseconds(series.Statistics.P50Ms, baselineMs),9}  {note}".TrimEnd());
        }

        foreach (var silent in step.Series.Where(r => r.Statistics.SampleCount == 0))
        {
            Console.WriteLine($"      {silent.Label,-26} {"—",9}  не ответил ни разу");
        }
    }

    /// <summary>
    /// Длительность с оглядкой на порог достоверности.
    /// </summary>
    /// <remarks>
    /// «0.0 мс» читается как «мгновенно», хотя означает «короче, чем измеритель умеет
    /// различать». Разница существенная: в первом случае фазы нет, во втором она есть,
    /// но её длительность неизвестна.
    /// </remarks>
    private static string Milliseconds(double ms, double baselineMs) =>
        ms > 0 && ms < baselineMs
            ? "< " + ProbeMetrics.Format("p50", baselineMs)
            : ProbeMetrics.Format("p50", ms);

    private static string Seconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " с";

    private static void WriteVerdicts(ScenarioRun run)
    {
        Console.WriteLine();

        foreach (var step in run.Steps)
        {
            Console.WriteLine($"  {VerdictWording.Mark(step.Verdict.Level)} {step.Name} — {VerdictWording.Outcome(step.Verdict.Level)}");
            Console.WriteLine($"      {step.Verdict.Summary}");

            if (step.Verdict.Explanation is { Length: > 0 } explanation)
            {
                Console.WriteLine($"      {explanation}");
            }

            // Пороги ставят на числа, а половина находок пробы числами не является:
            // расхождение резолверов, истекающий сертификат, код 5xx. Показать
            // «всё в норме» и промолчать о них значило бы соврать вердиктом.
            foreach (var warning in step.Warnings)
            {
                Console.WriteLine($"      ! {warning.Name}: {warning.Value}");
            }

            if (step.RunId is { } runId)
            {
                Console.WriteLine($"      подробности: storm runs show {runId.ToString()[..8]}");
            }
        }
    }

    /// <summary>
    /// Сводка по набору целей.
    /// </summary>
    /// <remarks>
    /// То, ради чего целей берут несколько. Одна упавшая цель из пяти означает поломку
    /// у неё; пять упавших из пяти — поломку у нас. Один и тот же вердикт по одной цели
    /// не отличает эти случаи никак, и таблица существует ровно для того, чтобы
    /// оператор увидел разницу, не сличая четыре простыни вывода глазами.
    /// </remarks>
    public static void WriteSetSummary(TargetSet set, IReadOnlyList<(string Target, ScenarioRun Run)> runs)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(runs);

        Console.WriteLine();
        Console.WriteLine($"=== {set.Title}: итог по {runs.Count} целям ===");
        Console.WriteLine();
        Console.WriteLine($"  {"цель",-30} {"итог",-16} где сломалось");

        foreach (var (target, run) in runs)
        {
            var where = run.FirstFailure?.Name ?? "—";

            Console.WriteLine($"  {target,-30} {VerdictWording.Mark(run.Level)} {VerdictWording.Outcome(run.Level),-14} {where}");
        }

        Console.WriteLine();

        var failed = runs.Count(r => r.Run.Level == VerdictLevel.Fail);

        Console.WriteLine(TargetSetConclusion.Describe(runs.Count, failed));
    }

    private static void WriteConclusion(ScenarioRun run)
    {
        Console.WriteLine();

        if (run.FirstFailure is { } failure)
        {
            Console.WriteLine($"Итог: не прошло. Сломалось на шаге «{failure.Name}»: {failure.Verdict.Summary}");

            var skipped = run.Steps.Count(s => s.WasSkipped);

            if (skipped > 0)
            {
                Console.WriteLine($"Следующие {Plural.With(skipped, "шаг", "шага", "шагов")} не выполнялись: "
                                  + "проверять их было не по чему.");
            }

            return;
        }

        Console.WriteLine(run.Level switch
        {
            VerdictLevel.Pass => "Итог: всё в норме.",
            VerdictLevel.Warn => "Итог: работает, но есть предупреждения — см. выше.",
            _ => "Итог: измерено, но не оценено — пороги не заданы.",
        });

        // Находки проб не поднимают уровень вердикта: уровень задают пороги, а порог
        // ставит человек. Но и промолчать нельзя — «всё в норме» рядом с невыясненным
        // расхождением ответов читается как «мы проверили, и это нормально».
        var findings = run.Steps.Sum(s => s.Warnings.Count);

        if (findings > 0 && run.Level == VerdictLevel.Pass)
        {
            Console.WriteLine($"Пороги соблюдены, но пробы отметили находок: {findings}. "
                              + "Порогами они не проверяются — это не числа.");
        }
    }
    /// <summary>
    /// Показывает всё, что можно запустить: шаблоны и собранное оператором.
    /// </summary>
    /// <remarks>
    /// Шаблоны идут первыми не по алфавиту, а по смыслу: они проверены и объяснены,
    /// а своё оператор собрал сам и знает про него всё. Начинающему нужны первые.
    /// </remarks>
    public static void WriteLibrary(IReadOnlyList<ScenarioEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var templates = entries.Where(e => e.IsTemplate).ToList();
        var custom = entries.Where(e => !e.IsTemplate).ToList();

        Console.WriteLine("Готовые сценарии:");
        Console.WriteLine();

        foreach (var entry in templates)
        {
            Console.WriteLine($"  {entry.Key,-10} {entry.Title}");
            Console.WriteLine($"             {entry.About}");
        }

        Console.WriteLine();

        if (custom.Count == 0)
        {
            Console.WriteLine("Своих сценариев нет. Собрать:");
            Console.WriteLine("  storm scenario new «моя проверка»        пустой");
            Console.WriteLine("  storm scenario from web «моя проверка»   копией шаблона");

            return;
        }

        Console.WriteLine("Ваши сценарии:");
        Console.WriteLine();

        foreach (var entry in custom)
        {
            Console.WriteLine($"  {entry.Key,-10} {entry.About}");
        }
    }

    /// <summary>
    /// Показывает сценарий по шагам — то, что редактируют.
    /// </summary>
    /// <remarks>
    /// Порядок шагов пронумерован, потому что именно номерами их переставляют
    /// и удаляют. Цель шага печатается всегда: у сравнения резолверов она у каждого
    /// шага своя, и не показать её значило бы скрыть главное отличие такого сценария.
    /// </remarks>
    public static void WriteDefinition(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        Console.WriteLine($"Сценарий  : {scenario.Name}");

        if (scenario.Description is { Length: > 0 } about)
        {
            Console.WriteLine($"Описание  : {about}");
        }

        Console.WriteLine($"Редакция  : {scenario.Version.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine();

        if (scenario.Steps.Count == 0)
        {
            Console.WriteLine("Шагов нет. Добавить: storm scenario step «имя» --проба ping --цель <адрес>");

            return;
        }

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];

            Console.WriteLine($"  {(i + 1).ToString(CultureInfo.InvariantCulture)}. {step.Name}"
                              + $"  ({step.ProbeName} → {step.Target.DisplayName})");

            if (step.Parameters.Count > 0)
            {
                Console.WriteLine("       параметры: "
                                  + string.Join(", ", step.Parameters.Select(p => $"{p.Key}={p.Value}")));
            }

            if (step.Thresholds.Count > 0)
            {
                Console.WriteLine("       пороги   : "
                                  + string.Join(", ", step.Thresholds.Select(t => t.Describe())));
            }
        }

        Console.WriteLine();
        Console.WriteLine("Порядок важен: шаги идут сверху вниз. Переставить — storm scenario move.");
    }

}
