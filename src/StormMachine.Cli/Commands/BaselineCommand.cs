using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Эталоны: <c>storm baseline</c>.
/// </summary>
/// <remarks>
/// Эталон отвечает на вопрос, ради которого измерения и повторяют: стало лучше
/// или хуже. Вместе с числами он запоминает условия, при которых снят, — без них
/// сравнение даёт красивые цифры, которых не было.
/// </remarks>
internal static class BaselineCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("baseline", "Эталоны: зафиксировать норму и сравнивать с ней.");

        command.Subcommands.Add(BuildCapture(services));
        command.Subcommands.Add(BuildShow(services));
        command.Subcommands.Add(BuildCompare(services));
        command.Subcommands.Add(BuildRemove(services));

        command.SetAction(async (_, cancellationToken) =>
        {
            var store = services.GetRequiredService<IBaselineStore>();

            BaselineRenderer.WriteList(
                await store.ListAsync(new BaselineQuery(), cancellationToken).ConfigureAwait(false));

            return 0;
        });

        return command;
    }

    private static Command BuildCapture(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Как назвать эталон." };

        var run = new Option<string?>("--прогон", "--run")
        {
            Description = "Идентификатор прогона или его начало. По умолчанию — последний прогон.",
        };

        var probe = new Option<string?>("--проба", "--probe")
        {
            Description = "Взять последний прогон этой пробы.",
        };

        var description = new Option<string?>("--описание", "--description")
        {
            Description = "Пояснение: при каких обстоятельствах снята норма.",
        };

        var command = new Command("capture", "Зафиксировать эталон по выполненному измерению.")
        {
            name, run, probe, description,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var runs = services.GetRequiredService<IRunStore>();
            var store = services.GetRequiredService<IBaselineStore>();

            var stored = await ResolveRunAsync(runs, parse.GetValue(run), parse.GetValue(probe), cancellationToken)
                .ConfigureAwait(false);

            if (stored is null)
            {
                return 1;
            }

            var metrics = ProbeMetrics.FromStored(stored.Series, stored.Facts);

            if (metrics.Count == 0)
            {
                Console.Error.WriteLine("У прогона нет ни одной метрики — фиксировать нечего.");

                return 1;
            }

            // Что годится в эталон, решает домен: счётчики проб — настройка замера,
            // а не свойство сети, и «отправлено 8 против 10» ухудшением не является.
            var chosen = metrics
                .Where(m => Baseline.IsComparable(m.Key))
                .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .Select(m => new BaselineMetric(
                    m.Key,
                    m.Value,
                    Baseline.HigherIsBetterFor(m.Key, stored.Unit)))
                .ToList();

            var existing = await store.FindAsync(parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            var baseline = new Baseline
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = parse.GetValue(name)!,
                Description = parse.GetValue(description),
                Subject = stored.Summary.ProbeName,
                Target = stored.Target,
                Context = stored.Context,
                Unit = stored.Unit,
                Metrics = chosen,
                RunId = stored.Summary.Id,
                CapturedUtc = DateTimeOffset.UtcNow,
            };

            await store.SaveAsync(baseline, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(existing is null
                ? $"Эталон «{baseline.Name}» зафиксирован."
                : $"Эталон «{baseline.Name}» перезаписан.");

            BaselineRenderer.WriteDetails(baseline);

            return 0;
        });

        return command;
    }

    private static Command BuildShow(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя эталона или его начало." };
        var command = new Command("show", "Показать эталон целиком.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IBaselineStore>();
            var baseline = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (baseline is null)
            {
                return 1;
            }

            BaselineRenderer.WriteDetails(baseline);

            return 0;
        });

        return command;
    }

    private static Command BuildCompare(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя эталона или его начало." };

        var run = new Option<string?>("--прогон", "--run")
        {
            Description = "С чем сравнивать. По умолчанию — последний прогон той же пробы.",
        };

        var command = new Command("compare", "Сравнить измерение с эталоном: было / стало.") { name, run };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IBaselineStore>();
            var runs = services.GetRequiredService<IRunStore>();

            var baseline = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (baseline is null)
            {
                return 1;
            }

            var stored = await ResolveRunAsync(runs, parse.GetValue(run), baseline.Subject, cancellationToken)
                .ConfigureAwait(false);

            if (stored is null)
            {
                return 1;
            }

            var comparison = BaselineComparer.Compare(
                baseline,
                ProbeMetrics.FromStored(stored.Series, stored.Facts),
                stored.Context);

            BaselineRenderer.WriteComparison(comparison, stored);

            return comparison.WorseCount > 0 ? 2 : 0;
        });

        return command;
    }

    private static Command BuildRemove(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя эталона или его начало." };
        var command = new Command("rm", "Удалить эталон.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IBaselineStore>();
            var baseline = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (baseline is null)
            {
                return 1;
            }

            await store.DeleteAsync(baseline.Id, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Эталон «{baseline.Name}» удалён. Прогон, с которого он снят, остался в журнале.");

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ помощники

    private static async Task<Baseline?> Find(
        IBaselineStore store,
        string needle,
        CancellationToken cancellationToken)
    {
        try
        {
            var baseline = await store.FindAsync(needle, cancellationToken).ConfigureAwait(false);

            if (baseline is null)
            {
                Console.Error.WriteLine($"Эталон «{needle}» не найден. Список: storm baseline.");
            }

            return baseline;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);

            return null;
        }
    }

    /// <summary>
    /// Находит прогон: по идентификатору, по пробе или просто последний.
    /// </summary>
    /// <remarks>
    /// Умолчание «последний прогон той же пробы» существует ради обычного случая:
    /// оператор только что померил и хочет сравнить. Заставлять его копировать
    /// идентификатор из журнала значило бы добавить шаг там, где ответ очевиден.
    /// </remarks>
    private static async Task<StoredRun?> ResolveRunAsync(
        IRunStore runs,
        string? id,
        string? probe,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var found = await runs
                .ListAsync(new RunQuery { Limit = 5000 }, cancellationToken)
                .ConfigureAwait(false);

            var matches = found
                .Where(r => r.Id.ToString().StartsWith(id.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                Console.Error.WriteLine($"Прогон «{id}» не найден. Список: storm runs.");

                return null;
            }

            if (matches.Count > 1)
            {
                Console.Error.WriteLine(
                    $"«{id}» подходит нескольким прогонам. Уточни идентификатор.");

                return null;
            }

            return await runs.GetAsync(matches[0].Id, cancellationToken).ConfigureAwait(false);
        }

        var query = new RunQuery { Limit = 1, ProbeName = probe };
        var latest = await runs.ListAsync(query, cancellationToken).ConfigureAwait(false);

        if (latest.Count == 0)
        {
            Console.Error.WriteLine(probe is null
                ? "В журнале нет ни одного прогона."
                : $"В журнале нет прогонов пробы «{probe}».");

            return null;
        }

        Console.WriteLine(
            $"Взят последний прогон: {latest[0].ProbeName} → {latest[0].TargetDisplay}, "
            + $"{latest[0].StartedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}");

        return await runs.GetAsync(latest[0].Id, cancellationToken).ConfigureAwait(false);
    }
}
