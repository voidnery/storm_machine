using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Topology;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Отчёты: <c>storm report</c>.
/// </summary>
/// <remarks>
/// Шаблонов четыре, потому что читателей четыре. Оператор выбирает не оформление,
/// а адресата: акт подписывают, сводку читает руководитель, технический разбирает
/// инженер, SLA показывают провайдеру.
/// </remarks>
internal static class ReportCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var template = new Argument<string>("шаблон")
        {
            Description = "technical, executive, acceptance или sla.",
            DefaultValueFactory = _ => "technical",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var runs = new Option<string[]>("--прогон", "--run")
        {
            Description = "Идентификатор прогона или его начало. Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var since = new Option<string?>("--за", "--since")
        {
            Description = "Взять все прогоны за срок: 24ч, 7д.",
        };

        var probe = new Option<string?>("--проба", "--probe") { Description = "Только прогоны этой пробы." };

        var monitor = new Option<string?>("--монитор", "--monitor")
        {
            Description = "Монитор для раздела о доступности. Обязателен для шаблона sla.",
        };

        var map = new Option<bool>("--карта", "--topology") { Description = "Вложить схему сети." };

        var baseline = new Option<string[]>("--эталон", "--baseline")
        {
            Description = "Сравнить с эталоном. Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var customer = new Option<string?>("--заказчик", "--customer") { Description = "Реквизит акта." };
        var site = new Option<string?>("--объект", "--site") { Description = "Площадка, филиал, помещение." };
        var author = new Option<string?>("--автор", "--author") { Description = "Кто выполнил проверку." };
        var title = new Option<string?>("--заголовок", "--title") { Description = "Заголовок документа." };

        var conclusion = new Option<string?>("--вывод", "--conclusion")
        {
            Description = "Заключение для акта. Продукт его не сочиняет — пишет человек.",
        };

        var output = new Option<string?>("--файл", "--out") { Description = "Куда сохранить." };

        var noCharts = new Option<bool>("--без-графиков", "--no-charts") { Description = "Не рисовать графики." };

        var command = new Command("report", "Документы: технический, сводка, акт, SLA.")
        {
            template, runs, since, probe, monitor, map, baseline,
            customer, site, author, title, conclusion, output, noCharts,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            if (!TryTemplate(parse.GetValue(template), out var chosen))
            {
                Console.Error.WriteLine(
                    "Шаблон должен быть одним из: technical, executive, acceptance, sla.");

                return 2;
            }

            var store = services.GetRequiredService<IRunStore>();

            // У шаблона SLA «--за» задаёт окно доступности, а не выборку измерений.
            // Подмешивать в него все прогоны за тот же срок значило бы приложить
            // к отчёту о мониторе сотню чужих измерений и назвать это основанием.
            var selected = chosen == ReportTemplate.ServiceLevel
                ? await SelectRunsAsync(store, parse, runs, null, null, cancellationToken).ConfigureAwait(false)
                : await SelectRunsAsync(store, parse, runs, since, probe, cancellationToken).ConfigureAwait(false);

            var level = await ServiceLevelAsync(services, parse.GetValue(monitor), parse.GetValue(since), cancellationToken)
                .ConfigureAwait(false);

            if (chosen == ReportTemplate.ServiceLevel && level is null)
            {
                Console.Error.WriteLine("Для шаблона sla нужен монитор: «--монитор <имя>».");

                return 2;
            }

            if (selected.Count == 0 && level is null)
            {
                Console.Error.WriteLine("Нечего показывать: прогоны не выбраны и монитор не задан.");

                return 2;
            }

            var comparisons = await CompareAsync(services, parse.GetValue(baseline) ?? [], selected, cancellationToken)
                .ConfigureAwait(false);

            var request = new ReportRequest
            {
                Template = chosen,
                Title = parse.GetValue(title),
                Author = parse.GetValue(author) ?? Environment.UserName,
                Customer = parse.GetValue(customer),
                Site = parse.GetValue(site),
                Conclusion = parse.GetValue(conclusion),
                Runs = selected,
                Topology = parse.GetValue(map)
                    ? await services.GetRequiredService<TopologyService>()
                        .BuildAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                    : null,
                ServiceLevel = level,
                Baselines = comparisons,
                IncludeCharts = !parse.GetValue(noCharts),
            };

            var renderer = services.GetRequiredService<IReportRenderer>();
            var report = await renderer.RenderAsync(request, cancellationToken).ConfigureAwait(false);

            var path = parse.GetValue(output) is { Length: > 0 } chosenPath
                ? chosenPath
                : report.SuggestedFileName;

            await File.WriteAllBytesAsync(path, report.Content, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Отчёт сохранён: {Path.GetFullPath(path)}");
            Console.WriteLine(
                $"  шаблон: {Describe(chosen)}; измерений: {selected.Count.ToString(CultureInfo.InvariantCulture)}"
                + (level is null ? string.Empty : "; раздел о доступности вложен")
                + (request.Topology is { IsEmpty: false } ? "; схема сети вложена" : string.Empty)
                + (comparisons.Count > 0
                    ? $"; сравнений с эталоном: {comparisons.Count.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty));

            // Технический отчёт разворачивает каждое измерение целиком. Пятьдесят
            // прогонов — это полсотни страниц, и узнать об этом лучше до открытия файла.
            if (chosen == ReportTemplate.Technical && selected.Count > 20)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"Технический отчёт разворачивает каждое измерение: {selected.Count.ToString(CultureInfo.InvariantCulture)} "
                    + "прогонов дадут документ на столько же разделов.");
                Console.WriteLine("Для сводной таблицы есть шаблоны executive и acceptance.");
            }

            if (chosen == ReportTemplate.Acceptance && string.IsNullOrWhiteSpace(parse.GetValue(conclusion)))
            {
                Console.WriteLine();
                Console.WriteLine("Заключение не заполнено — в акте на его месте пояснение.");
                Console.WriteLine("Продукт вывод не сочиняет: оценку пригодности даёт подписавший.");
            }

            return 0;
        });

        return command;
    }

    private static bool TryTemplate(string? text, out ReportTemplate template)
    {
        template = ReportTemplate.Technical;

        return text?.Trim().ToLowerInvariant() switch
        {
            null or "" or "technical" or "техника" => true,
            "executive" or "сводка" => Set(ReportTemplate.Executive, out template),
            "acceptance" or "акт" => Set(ReportTemplate.Acceptance, out template),
            "sla" or "доступность" => Set(ReportTemplate.ServiceLevel, out template),
            _ => false,
        };

        static bool Set(ReportTemplate value, out ReportTemplate target)
        {
            target = value;

            return true;
        }
    }

    /// <summary>
    /// Какие прогоны попадут в документ.
    /// </summary>
    /// <remarks>
    /// Явные идентификаторы имеют приоритет над сроком и пробой: если оператор
    /// перечислил прогоны, он знает, что хочет видеть, и добавлять к ним лишнее —
    /// значит подменять его выбор.
    /// </remarks>
    private static async Task<List<StoredRun>> SelectRunsAsync(
        IRunStore store,
        ParseResult parse,
        Option<string[]> runs,
        Option<string?>? since,
        Option<string?>? probe,
        CancellationToken cancellationToken)
    {
        var ids = parse.GetValue(runs) ?? [];
        var all = await store.ListAsync(new RunQuery { Limit = 5000 }, cancellationToken).ConfigureAwait(false);
        var chosen = new List<RunSummary>();

        if (ids.Length > 0)
        {
            foreach (var id in ids)
            {
                var matches = all
                    .Where(r => r.Id.ToString().StartsWith(id.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 1)
                {
                    chosen.Add(matches[0]);
                }
                else
                {
                    Console.Error.WriteLine(matches.Count == 0
                        ? $"Прогон «{id}» не найден."
                        : $"«{id}» подходит нескольким прогонам — уточни идентификатор.");
                }
            }
        }
        else if (since is not null || probe is not null)
        {
            var from = since is not null && Schedule.TryParseInterval(parse.GetValue(since), out var span)
                ? DateTimeOffset.UtcNow - span
                : (DateTimeOffset?)null;

            var name = probe is null ? null : parse.GetValue(probe);

            chosen.AddRange(all
                .Where(r => from is not { } moment || r.StartedUtc >= moment)
                .Where(r => name is null || string.Equals(r.ProbeName, name, StringComparison.OrdinalIgnoreCase))
                .Take(from is null && name is null ? 1 : 200));
        }

        var loaded = new List<StoredRun>(chosen.Count);

        foreach (var summary in chosen.OrderBy(r => r.StartedUtc))
        {
            if (await store.GetAsync(summary.Id, cancellationToken).ConfigureAwait(false) is { } run)
            {
                loaded.Add(run);
            }
        }

        return loaded;
    }

    private static async Task<ServiceLevelSection?> ServiceLevelAsync(
        IServiceProvider services,
        string? name,
        string? since,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var monitors = services.GetRequiredService<IMonitorStore>();
        var monitor = await monitors.FindAsync(name, cancellationToken).ConfigureAwait(false);

        if (monitor is null)
        {
            Console.Error.WriteLine($"Монитор «{name}» не найден. Список: storm monitors.");

            return null;
        }

        var span = Schedule.TryParseInterval(since, out var parsed)
            ? parsed
            : monitor.Objective?.Window ?? TimeSpan.FromDays(7);

        var now = DateTimeOffset.UtcNow;
        var from = now - span;

        var checks = await monitors
            .ListChecksAsync(new CheckQuery { MonitorId = monitor.Id, Since = from, Limit = 100_000 }, cancellationToken)
            .ConfigureAwait(false);

        return new ServiceLevelSection(
            monitor,
            AvailabilityCalculator.Compute(checks, from, now, monitor.Objective),
            checks);
    }

    private static async Task<List<BaselineComparison>> CompareAsync(
        IServiceProvider services,
        string[] names,
        List<StoredRun> runs,
        CancellationToken cancellationToken)
    {
        var comparisons = new List<BaselineComparison>();

        if (names.Length == 0 || runs.Count == 0)
        {
            return comparisons;
        }

        var store = services.GetRequiredService<IBaselineStore>();

        foreach (var name in names)
        {
            var baseline = await store.FindAsync(name, cancellationToken).ConfigureAwait(false);

            if (baseline is null)
            {
                Console.Error.WriteLine($"Эталон «{name}» не найден — раздел сравнения пропущен.");

                continue;
            }

            // Сравнивается прогон той же пробы: сопоставлять эталон ping с прогоном
            // http бессмысленно, и молча выбрать первый попавшийся значило бы выдать
            // бессмыслицу за вывод.
            var run = runs.LastOrDefault(r =>
                string.Equals(r.Summary.ProbeName, baseline.Subject, StringComparison.OrdinalIgnoreCase));

            if (run is null)
            {
                Console.Error.WriteLine(
                    $"Эталон «{baseline.Name}» снят пробой «{baseline.Subject}», "
                    + "а среди выбранных прогонов такой нет — раздел сравнения пропущен.");

                continue;
            }

            comparisons.Add(BaselineComparer.Compare(
                baseline,
                ProbeMetrics.FromStored(run.Series, run.Facts),
                run.Context));
        }

        return comparisons;
    }

    private static string Describe(ReportTemplate template) => template switch
    {
        ReportTemplate.Executive => "сводка",
        ReportTemplate.Acceptance => "акт тестирования",
        ReportTemplate.ServiceLevel => "доступность (SLA)",
        _ => "технический",
    };
}
